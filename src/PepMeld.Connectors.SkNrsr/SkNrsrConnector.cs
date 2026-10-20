using System.Globalization;
using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.SkNrsr;

/// <summary>
/// The National Council's own web site: the alphabetical list of deputies per term,
/// and a profile per deputy with the date of birth and the town of residence. The only
/// Slovak source found that publishes the date of birth for a whole category of
/// § 6 ods. 2 (letter b).
///
/// There is no export, so the "file" this connector downloads is one it assembles: a zip
/// with the list page of every term and the profile page of every deputy in them,
/// which is what the snapshot keeps and what Parse reads. Profiles are cached on disk
/// between runs (see <see cref="SkNrsrOptions.ProfileCacheDays"/>), lists never are.
/// </summary>
public sealed class SkNrsrConnector(
    IHttpClientFactory httpClients,
    IOptions<SkNrsrOptions> options,
    IOptions<PepMeldOptions> pepMeld,
    TimeProvider clock,
    ILogger<SkNrsrConnector> logger) : IPepListConnector
{
    public const string Code = "SK_NRSR";
    public const string HttpClientName = "PepMeld.SkNrsr";

    // Pinned, so the same pages give the same bytes and the unchanged check can work.
    private static readonly DateTimeOffset EntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string SourceCode => Code;

    public async Task<ListDownload> DownloadAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var terms = NrsrTerms.Parse(settings.Terms);
        var client = httpClients.CreateClient(HttpClientName);
        var delay = TimeSpan.FromMilliseconds(settings.DelayMilliseconds);

        var temp = Path.GetTempFileName();

        try
        {
            // The newest term a deputy appears in: the profile is fetched under it, and
            // the list of that term is the URL the snapshot is attributed to.
            var newestTermOf = new Dictionary<int, int>();

            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                foreach (var term in terms)
                {
                    var html = await GetAsync(client, ListUrl(term), ct).ConfigureAwait(false);
                    await WriteAsync(archive, $"list-{term}.html", html, ct).ConfigureAwait(false);

                    foreach (var deputy in NrsrHtmlParser.ParseList(html))
                    {
                        newestTermOf[deputy.Id] = term;
                    }

                    await Task.Delay(delay, clock, ct).ConfigureAwait(false);
                }

                var cacheDirectory = Path.Combine(Path.GetFullPath(pepMeld.Value.Ingest.SnapshotStoragePath), Code, "profile-cache");
                Directory.CreateDirectory(cacheDirectory);
                var fetched = 0;

                foreach (var (id, term) in newestTermOf.OrderBy(x => x.Key))
                {
                    var cached = Path.Combine(cacheDirectory, $"{id}.html");
                    string html;

                    if (File.Exists(cached) && clock.GetUtcNow() - File.GetLastWriteTimeUtc(cached) < TimeSpan.FromDays(settings.ProfileCacheDays))
                    {
                        html = await File.ReadAllTextAsync(cached, ct).ConfigureAwait(false);
                    }
                    else
                    {
                        html = await GetAsync(client, ProfileUrl(id, term), ct).ConfigureAwait(false);
                        await File.WriteAllTextAsync(cached, html, ct).ConfigureAwait(false);
                        fetched++;
                        await Task.Delay(delay, clock, ct).ConfigureAwait(false);
                    }

                    await WriteAsync(archive, $"profile-{id}.html", html, ct).ConfigureAwait(false);
                }

                logger.LogInformation("NR SR: {Terms} term list(s), {Deputies} deputies, {Fetched} profiles fetched, the rest from the cache.",
                    terms.Count, newestTermOf.Count, fetched);
            }

            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
            return new ListDownload(stream, ListUrl(terms[^1]), null);
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

        var listEntries = archive.Entries
            .Select(e => (Entry: e, Term: TermOf(e.Name)))
            .Where(x => x.Term is not null)
            .OrderBy(x => x.Term)
            .ToList();

        var membership = new Dictionary<int, (string ListedName, SortedSet<int> Terms)>();

        foreach (var (entry, term) in listEntries)
        {
            foreach (var deputy in NrsrHtmlParser.ParseList(Read(entry)))
            {
                if (!membership.TryGetValue(deputy.Id, out var known))
                {
                    membership[deputy.Id] = known = (deputy.ListedName, []);
                }

                known.Terms.Add(term!.Value);
            }
        }

        var persons = new List<ParsedPerson>(membership.Count);

        foreach (var (id, (listedName, terms)) in membership.OrderBy(x => x.Key))
        {
            var newest = terms.Max;
            var profile = archive.GetEntry($"profile-{id}.html") is { } entry ? NrsrHtmlParser.ParseProfile(Read(entry)) : null;

            string given, family;
            if (profile is not null)
            {
                given = profile.GivenName;
                family = profile.FamilyName;
            }
            else
            {
                // The list writes "Priezvisko, Meno"; without a profile that is all there is.
                logger.LogWarning("NR SR: no readable profile for deputy {Id} ({Name}); imported from the list alone, without a date of birth.", id, listedName);
                var comma = listedName.IndexOf(',', StringComparison.Ordinal);
                family = comma > 0 ? listedName[..comma].Trim() : listedName;
                given = comma > 0 ? listedName[(comma + 1)..].Trim() : string.Empty;
            }

            var functions = terms.Select(term =>
            {
                DateOnly? from = null, to = null;
                if (NrsrTerms.Bounds.TryGetValue(term, out var bounds))
                {
                    (from, to) = bounds;
                }

                return new ParsedFunction(
                    $"term:{term}",
                    Country.SK,
                    PepCategory.MemberOfParliament,
                    $"Národná rada Slovenskej republiky ({term}. volebné obdobie)",
                    "poslanec Národnej rady SR",
                    from,
                    to,
                    ProfileUrl(id, term))
                {
                    DateNote = NrsrTerms.DateNote,
                };
            })
            .OrderByDescending(f => f.To is null)
            .ThenByDescending(f => f.From)
            .ToList();

            persons.Add(new ParsedPerson(
                id.ToString(CultureInfo.InvariantCulture),
                given,
                family,
                profile?.TitleBefore,
                profile?.TitleAfter,
                profile?.BirthDate,
                null,
                ProfileUrl(id, newest),
                functions)
            {
                Residence = profile?.Residence,
            });
        }

        return persons;
    }

    private string ListUrl(int term) =>
        $"{options.Value.BaseUrl}Default.aspx?sid=poslanci/zoznam_abc&ListType=0&CisObdobia={term}";

    private string ProfileUrl(int id, int term) =>
        $"{options.Value.BaseUrl}Default.aspx?sid=poslanci/poslanec&PoslanecID={id}&CisObdobia={term}";

    private static int? TermOf(string entryName) =>
        entryName.StartsWith("list-", StringComparison.Ordinal) && entryName.EndsWith(".html", StringComparison.Ordinal)
            && int.TryParse(entryName[5..^5], NumberStyles.Integer, CultureInfo.InvariantCulture, out var term)
            ? term
            : null;

    private static async Task<string> GetAsync(HttpClient client, string url, CancellationToken ct)
    {
        using var response = await client.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    private static async Task WriteAsync(ZipArchive archive, string name, string html, CancellationToken ct)
    {
        var entry = archive.CreateEntry(name, CompressionLevel.Optimal);
        entry.LastWriteTime = EntryTime;
        await using var target = entry.Open();
        await using var writer = new StreamWriter(target, System.Text.Encoding.UTF8);
        await writer.WriteAsync(html.AsMemory(), ct).ConfigureAwait(false);
    }

    private static string Read(ZipArchiveEntry entry)
    {
        using var stream = entry.Open();
        using var reader = new StreamReader(stream, System.Text.Encoding.UTF8);
        return reader.ReadToEnd();
    }
}
