using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using SancMeld.Core.Matching;

namespace PepMeld.Connectors.CzAres;

/// <summary>
/// The boards of Czech state enterprises and state-owned companies, read from ARES, the
/// public register run by the Ministry of Finance. Letter g) of the Czech list (and of
/// SK § 6 ods. 2): members of the managing and supervisory bodies of a corporation the
/// state controls. ARES gives every member with the date of birth and the dates of the
/// seat, so these are level A findings, the only ones of their kind in the product.
///
/// Which companies: every subject of the state-enterprise legal forms, found by the
/// register's own search, plus the companies in StateEntities.json, which the state
/// owns as a shareholder and which no search can find. The snapshot is a zip of every
/// record read, so a check can be replayed against the register as it was.
/// </summary>
public sealed class CzAresStateBoardsConnector(
    IHttpClientFactory httpClients,
    IOptions<CzAresOptions> options,
    TimeProvider clock,
    ILogger<CzAresStateBoardsConnector> logger) : IPepListConnector
{
    public const string Code = "CZ_ARES_STATE";
    public const string HttpClientName = "PepMeld.CzAres";

    /// <summary>The recipe for a person's key: bump when it changes, so old keys are known to be of another recipe.</summary>
    public const int FingerprintVersion = 1;

    private const string EntitiesEntry = "entities.json";
    private static readonly DateTimeOffset EntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string SourceCode => Code;

    public async Task<ListDownload> DownloadAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var client = httpClients.CreateClient(HttpClientName);
        var delay = TimeSpan.FromMilliseconds(settings.DelayMilliseconds);
        var temp = Path.GetTempFileName();

        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                var entities = new Dictionary<string, StateEntity>(StringComparer.Ordinal);

                foreach (var entity in StateEntities.Load())
                {
                    entities[entity.Ico] = entity;
                }

                foreach (var legalForm in settings.LegalForms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    foreach (var (ico, name) in await SearchByLegalFormAsync(client, legalForm, delay, ct).ConfigureAwait(false))
                    {
                        entities.TryAdd(ico, new StateEntity(ico, name, $"právní forma {legalForm}"));
                    }
                }

                if (entities.Count == 0)
                {
                    throw new InvalidDataException("ARES returned no state enterprise at all; the search endpoint has probably changed.");
                }

                var missing = 0;
                foreach (var entity in entities.Values.OrderBy(e => e.Ico, StringComparer.Ordinal))
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, $"{settings.BaseUrl.TrimEnd('/')}/ekonomicke-subjekty-vr/{Uri.EscapeDataString(entity.Ico)}");
                    request.Headers.TryAddWithoutValidation("Accept", "application/json");

                    using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.NotFound)
                    {
                        // A state enterprise in liquidation may have left the court register.
                        missing++;
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    var json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    await WriteAsync(archive, $"vr-{entity.Ico}.json", json, ct).ConfigureAwait(false);
                    await Task.Delay(delay, clock, ct).ConfigureAwait(false);
                }

                await WriteAsync(archive, EntitiesEntry, JsonSerializer.Serialize(entities.Values.OrderBy(e => e.Ico, StringComparer.Ordinal)), ct).ConfigureAwait(false);
                logger.LogInformation("ARES state boards: {Entities} companies, {Missing} without a public-register record.", entities.Count, missing);
            }

            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
            return new ListDownload(stream, $"{settings.BaseUrl.TrimEnd('/')}/ekonomicke-subjekty-vr/", null);
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

        var entities = archive.GetEntry(EntitiesEntry) is { } e
            ? (JsonSerializer.Deserialize<List<StateEntity>>(Read(e)) ?? []).ToDictionary(x => x.Ico, StringComparer.Ordinal)
            : [];

        var horizon = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime).AddYears(-options.Value.HistoryYears);
        var byPerson = new Dictionary<string, (AresSeat First, List<ParsedFunction> Functions)>(StringComparer.Ordinal);

        foreach (var entry in archive.Entries.Where(x => x.Name.StartsWith("vr-", StringComparison.Ordinal) && x.Name.EndsWith(".json", StringComparison.Ordinal)))
        {
            using var document = JsonDocument.Parse(Read(entry));

            foreach (var seat in AresBoardParser.Seats(document.RootElement))
            {
                if (seat.To is { } ended && ended < horizon)
                {
                    continue;
                }

                var key = Fingerprint(seat);
                if (!byPerson.TryGetValue(key, out var person))
                {
                    byPerson[key] = person = (seat, []);
                }

                var reason = entities.TryGetValue(seat.Ico, out var entity) ? entity.Why : null;
                var institution = reason is null
                    ? $"{seat.CompanyName} (IČO {seat.Ico})"
                    : $"{seat.CompanyName} (IČO {seat.Ico}; {reason})";

                person.Functions.Add(new ParsedFunction(
                    $"{seat.Ico}:{NameNormalizer.Normalize(seat.Body + " " + seat.Role).Trim()}:{seat.From?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "?"}",
                    Country.CZ,
                    PepCategory.StateEnterpriseBoard,
                    institution,
                    seat.Role,
                    seat.From,
                    seat.To,
                    $"https://ares.gov.cz/ekonomicke-subjekty?ico={seat.Ico}"));
            }
        }

        return byPerson
            .OrderBy(p => p.Key, StringComparer.Ordinal)
            .Select(p => new ParsedPerson(
                p.Key,
                p.Value.First.GivenName,
                p.Value.First.Surname,
                p.Value.First.TitleBefore,
                p.Value.First.TitleAfter,
                p.Value.First.BirthDate,
                null,
                $"https://ares.gov.cz/ekonomicke-subjekty?ico={p.Value.First.Ico}",
                p.Value.Functions
                    .DistinctBy(f => f.SourceKey, StringComparer.Ordinal)
                    .OrderByDescending(f => f.To is null)
                    .ThenByDescending(f => f.From)
                    .ToList())
            {
                Residence = p.Value.First.Residence,
            })
            .ToList();
    }

    /// <summary>
    /// ARES has no identifier for a person; the same human on two boards is two entries.
    /// The key is the normalised surname, given name and date of birth, which is what the
    /// register itself uses to tell people apart, so one person's seats come together.
    /// </summary>
    internal static string Fingerprint(AresSeat seat) =>
        $"v{FingerprintVersion}:{NameNormalizer.Normalize(seat.Surname).Trim()}|{NameNormalizer.Normalize(seat.GivenName).Trim()}|{seat.BirthDate:yyyy-MM-dd}";

    private async Task<List<(string Ico, string Name)>> SearchByLegalFormAsync(HttpClient client, string legalForm, TimeSpan delay, CancellationToken ct)
    {
        const int pageSize = 200;
        var result = new List<(string, string)>();

        for (var start = 0; ; start += pageSize)
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.BaseUrl.TrimEnd('/')}/ekonomicke-subjekty/vyhledat")
            {
                Content = JsonContent.Create(new { pravniForma = new[] { legalForm }, pocet = pageSize, start }),
            };
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            using var response = await client.SendAsync(request, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var total = document.RootElement.TryGetProperty("pocetCelkem", out var t) ? t.GetInt32() : 0;
            var page = 0;

            if (document.RootElement.TryGetProperty("ekonomickeSubjekty", out var subjects) && subjects.ValueKind == JsonValueKind.Array)
            {
                foreach (var subject in subjects.EnumerateArray())
                {
                    page++;
                    // Subjects without a real IČO carry an "ARES_…" placeholder; they have
                    // no public-register record to read.
                    if (subject.TryGetProperty("icoId", out var ico) && ico.GetString() is { Length: 8 } icoText && icoText.All(char.IsAsciiDigit))
                    {
                        result.Add((icoText, subject.TryGetProperty("obchodniJmeno", out var n) ? n.GetString() ?? icoText : icoText));
                    }
                }
            }

            await Task.Delay(delay, clock, ct).ConfigureAwait(false);

            if (page == 0 || start + page >= total)
            {
                return result;
            }
        }
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
