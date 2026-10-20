using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.Wikidata;

/// <summary>
/// Holders of prominent public functions as Wikidata records them: one SPARQL query per
/// position, every holder with a date of birth and the dates of each term. CC0, so
/// nothing to license; edited by anyone, so the source is marked unofficial and a match
/// on it is level B, cited with the item so a reader can see who wrote what.
///
/// One instance per source, Czech and Slovak, each with its own positions. Only persons
/// whose date of birth is known to the day are read: a year alone cannot confirm or
/// exclude a namesake, and a function without a usable date would be a level C finding
/// from a source that is not even official.
/// </summary>
public sealed class WikidataPositionsConnector(
    string sourceCode,
    Country country,
    IHttpClientFactory httpClients,
    IOptions<WikidataPositionsOptions> options,
    TimeProvider clock,
    ILogger<WikidataPositionsConnector> logger) : IPepListConnector
{
    public const string CzechCode = "CZ_WIKIDATA";
    public const string SlovakCode = "SK_WIKIDATA";
    public const string HttpClientName = "PepMeld.Wikidata";

    private static readonly DateTimeOffset EntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string SourceCode => sourceCode;

    private IReadOnlyList<WikidataPosition> Positions =>
        options.Value.Sources.TryGetValue(sourceCode, out var positions) ? positions : [];

    /// <summary>Labels in the country's language first; Czech and Slovak read each other's.</summary>
    private string Languages => country == Country.SK ? "sk,cs,en" : "cs,sk,en";

    public async Task<ListDownload> DownloadAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var client = httpClients.CreateClient(HttpClientName);
        var temp = Path.GetTempFileName();

        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var position in Positions)
                {
                    var url = $"{settings.SparqlEndpoint}?query={Uri.EscapeDataString(Query(position, Languages))}";
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.TryAddWithoutValidation("Accept", "application/sparql-results+json");

                    using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                    response.EnsureSuccessStatusCode();

                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    await WriteAsync(archive, $"positions-{position.Key}.json", json, ct).ConfigureAwait(false);
                    await Task.Delay(TimeSpan.FromMilliseconds(settings.DelayMilliseconds), clock, ct).ConfigureAwait(false);
                }
            }

            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
            return new ListDownload(stream, settings.SparqlEndpoint, null);
        }
        catch
        {
            File.Delete(temp);
            throw;
        }
    }

    public IReadOnlyList<ParsedPerson> Parse(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);

        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        var byPerson = new Dictionary<string, (string Label, DateOnly Born, List<ParsedFunction> Functions)>(StringComparer.Ordinal);

        foreach (var position in Positions)
        {
            var entry = archive.GetEntry($"positions-{position.Key}.json");
            if (entry is null)
            {
                logger.LogWarning("Wikidata {Source}: the snapshot has no result for {Position}; its holders are not in this import.", sourceCode, position.Key);
                continue;
            }

            using var document = JsonDocument.Parse(Read(entry));
            var skippedPrecision = 0;

            foreach (var row in SparqlResults.Rows(document.RootElement))
            {
                var item = SparqlResults.EntityId(row, "p");
                var label = SparqlResults.Value(row, "pLabel");
                var born = SparqlResults.Date(row, "dob");
                var positionItem = SparqlResults.EntityId(row, "pos") ?? position.Item;

                if (item is null || label is null || born is null)
                {
                    continue;
                }

                // Precision 11 is a day; 10 a month, 9 a year.
                if (SparqlResults.Value(row, "dobPrecision") is { } precision && int.TryParse(precision, out var p) && p < 11)
                {
                    skippedPrecision++;
                    continue;
                }

                if (!byPerson.TryGetValue(item, out var person))
                {
                    byPerson[item] = person = (label, born.Value, []);
                }

                var start = SparqlResults.Date(row, "start");
                var end = SparqlResults.Date(row, "end");
                var role = position.Role is { Length: > 0 } fixedRole ? fixedRole : SparqlResults.Value(row, "posLabel") ?? positionItem;

                person.Functions.Add(new ParsedFunction(
                    $"{positionItem}:{start?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?"}",
                    country,
                    position.Category,
                    position.Institution,
                    role,
                    start,
                    end,
                    $"https://www.wikidata.org/wiki/{item}")
                {
                    DateNote = "Dátumy mandátu a dátum narodenia podľa Wikidata, zdroja, ktorý môže upravovať ktokoľvek.",
                });
            }

            if (skippedPrecision > 0)
            {
                logger.LogInformation("Wikidata {Position}: {Count} holder(s) left out, date of birth not known to the day.", position.Key, skippedPrecision);
            }
        }

        return byPerson
            .OrderBy(x => x.Key, StringComparer.Ordinal)
            .Select(x =>
            {
                var (given, family) = SplitLabel(x.Value.Label);
                return new ParsedPerson(
                    x.Key,
                    given,
                    family,
                    null,
                    null,
                    x.Value.Born,
                    null,
                    $"https://www.wikidata.org/wiki/{x.Key}",
                    x.Value.Functions
                        .DistinctBy(f => f.SourceKey, StringComparer.Ordinal)
                        .OrderByDescending(f => f.To is null)
                        .ThenByDescending(f => f.From)
                        .ToList());
            })
            .ToList();
    }

    /// <summary>
    /// Every holder of the position with a date of birth, one row per term. The date is
    /// read through its value node to get the precision; labels in the languages given,
    /// whichever exists first. The position comes back too, so a family of positions
    /// (every minister of a country) yields each one's own label as the role.
    /// </summary>
    internal static string Query(WikidataPosition position, string languages)
    {
        ArgumentNullException.ThrowIfNull(position);

        var positions = position.Jurisdiction is { Length: > 0 } jurisdiction
            ? $"?pos wdt:P279* wd:{position.Item} . ?pos wdt:P1001 wd:{jurisdiction} ."
            : position.IncludeSubclasses
                ? $"?pos wdt:P279* wd:{position.Item} ."
                : $"BIND(wd:{position.Item} AS ?pos)";

        var citizenship = position.Citizenship is { Length: > 0 } c ? $"?p wdt:P27 wd:{c} ." : string.Empty;

        return $$"""
            SELECT ?p ?pLabel ?pos ?posLabel ?dob ?dobPrecision ?start ?end WHERE {
              {{positions}}
              ?p p:P39 ?st . ?st ps:P39 ?pos .
              {{citizenship}}
              ?p p:P569/psv:P569 ?dobNode .
              ?dobNode wikibase:timeValue ?dob ; wikibase:timePrecision ?dobPrecision .
              OPTIONAL { ?st pq:P580 ?start }
              OPTIONAL { ?st pq:P582 ?end }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "{{languages}}". }
            }
            ORDER BY ?p ?pos ?start
            """;
    }

    /// <summary>"Miloš Vystrčil": the last word is the family name, the rest the given names. Good enough for a label; the item is the key.</summary>
    internal static (string Given, string Family) SplitLabel(string label)
    {
        var words = label.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return words.Length <= 1 ? (string.Empty, label.Trim()) : (string.Join(' ', words[..^1]), words[^1]);
    }

    private static async Task WriteAsync(ZipArchive archive, string name, string json, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        await using var target = entry.Open();
        await using var writer = new StreamWriter(target, System.Text.Encoding.UTF8);
        await writer.WriteAsync(json.AsMemory(), ct).ConfigureAwait(false);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}

/// <summary>The SPARQL JSON results format, reduced to what the query above asks for.</summary>
internal static class SparqlResults
{
    public static IEnumerable<JsonElement> Rows(JsonElement root) =>
        root.TryGetProperty("results", out var results) && results.TryGetProperty("bindings", out var bindings) && bindings.ValueKind == JsonValueKind.Array
            ? bindings.EnumerateArray()
            : [];

    public static string? Value(JsonElement row, string variable) =>
        row.TryGetProperty(variable, out var v) && v.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    /// <summary>"http://www.wikidata.org/entity/Q42" to "Q42".</summary>
    public static string? EntityId(JsonElement row, string variable) =>
        Value(row, variable) is { } uri && uri.LastIndexOf('/') is var slash && slash >= 0 ? uri[(slash + 1)..] : null;

    /// <summary>"1951-12-19T00:00:00Z" to the date; a negative year or anything else is null.</summary>
    public static DateOnly? Date(JsonElement row, string variable) =>
        Value(row, variable) is { Length: >= 10 } s && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
