using System.IO.Compression;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using SancMeld.Core.Matching;

namespace PepMeld.Connectors.SkNrsrVnf;

/// <summary>
/// The register of public officials kept by the National Council's committee on
/// incompatibility of functions (ústavný zákon č. 357/2004 Z. z.). Every official who
/// files a declaration is listed, and the declaration names every function held,
/// with the organisation: ministers and state secretaries, judges of the high courts,
/// prosecutors, the central bank's board, mayors and regional chairs, and the boards of
/// state enterprises and state-owned companies. That last group is letter g) of
/// § 6 ods. 2, which no other Slovak source lists by name.
///
/// No date of birth and no dates of office: a finding from here is level C, and its
/// dates are the import's. The site's view "by function" times out on the server for
/// the biggest functions, so the connector reads the alphabetical list (one form post)
/// and then each official's declaration page, cached for a month.
/// </summary>
public sealed class SkNrsrVnfConnector(
    IHttpClientFactory httpClients,
    IOptions<SkNrsrVnfOptions> options,
    IOptions<PepMeldOptions> pepMeld,
    TimeProvider clock,
    ILogger<SkNrsrVnfConnector> logger) : IPepListConnector
{
    public const string Code = "SK_NRSR_VNF";
    public const string HttpClientName = "PepMeld.SkNrsrVnf";

    private const string ListEntry = "zoznam-abc.html";
    private static readonly DateTimeOffset EntryTime = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public string SourceCode => Code;

    public async Task<ListDownload> DownloadAsync(CancellationToken ct)
    {
        var settings = options.Value;
        var client = httpClients.CreateClient(HttpClientName);
        var delay = TimeSpan.FromMilliseconds(settings.DelayMilliseconds);
        var listUrl = ListUrl();

        var temp = Path.GetTempFileName();

        try
        {
            await using (var file = new FileStream(temp, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            using (var archive = new ZipArchive(file, ZipArchiveMode.Create, leaveOpen: false))
            {
                // The alphabetical view is the answer to the page's own form: the hidden
                // fields of a fresh GET, the view selector, and the button that shows it.
                var page = await GetAsync(client, listUrl, ct).ConfigureAwait(false);
                var form = new Dictionary<string, string>(VnfHtmlParser.HiddenFields(page), StringComparer.Ordinal)
                {
                    ["_sectionLayoutContainer$ctl01$ViewsList"] = "1",
                    ["_sectionLayoutContainer$ctl01$ShowSelectedView"] = "Zobraziť",
                };

                using var response = await client.PostAsync(listUrl, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                var list = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                var officials = VnfHtmlParser.ParseList(list);

                if (officials.Count == 0)
                {
                    throw new InvalidDataException("The alphabetical list of public officials came back without a single official; the page's form has probably changed.");
                }

                await WriteAsync(archive, ListEntry, list, ct).ConfigureAwait(false);
                await Task.Delay(delay, clock, ct).ConfigureAwait(false);

                var cacheDirectory = Path.Combine(Path.GetFullPath(pepMeld.Value.Ingest.SnapshotStoragePath), Code, "declaration-cache");
                Directory.CreateDirectory(cacheDirectory);
                var fetched = 0;

                // Several lanes against the site, one writer into the zip: ZipArchive is
                // not thread-safe, and the entries are written in list order so the same
                // pages give the same bytes whichever lane finished first.
                var pages = new string[officials.Count];
                var lanes = new SemaphoreSlim(settings.Concurrency, settings.Concurrency);

                await Task.WhenAll(officials.Select(async (official, index) =>
                {
                    await lanes.WaitAsync(ct).ConfigureAwait(false);

                    try
                    {
                        var cached = Path.Combine(cacheDirectory, CacheName(official.UserId));

                        if (File.Exists(cached) && clock.GetUtcNow() - File.GetLastWriteTimeUtc(cached) < TimeSpan.FromDays(settings.DeclarationCacheDays))
                        {
                            pages[index] = await File.ReadAllTextAsync(cached, ct).ConfigureAwait(false);
                            return;
                        }

                        var html = await GetAsync(client, DeclarationUrl(official.UserId), ct).ConfigureAwait(false);
                        await File.WriteAllTextAsync(cached, html, ct).ConfigureAwait(false);
                        pages[index] = html;
                        Interlocked.Increment(ref fetched);
                        await Task.Delay(delay, clock, ct).ConfigureAwait(false);
                    }
                    finally
                    {
                        lanes.Release();
                    }
                })).ConfigureAwait(false);

                for (var i = 0; i < officials.Count; i++)
                {
                    await WriteAsync(archive, DeclarationEntry(officials[i].UserId), pages[i], ct).ConfigureAwait(false);
                }

                logger.LogInformation("NR SR public officials: {Officials} listed, {Fetched} declarations fetched, the rest from the cache.", officials.Count, fetched);
            }

            var stream = new FileStream(temp, FileMode.Open, FileAccess.Read, FileShare.None, 64 * 1024, FileOptions.DeleteOnClose);
            return new ListDownload(stream, listUrl, null);
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
        var list = archive.GetEntry(ListEntry) ?? throw new InvalidDataException($"The snapshot has no '{ListEntry}'.");
        var persons = new List<ParsedPerson>();

        foreach (var official in VnfHtmlParser.ParseList(Read(list)))
        {
            var declaration = archive.GetEntry(DeclarationEntry(official.UserId)) is { } entry
                ? VnfHtmlParser.ParseDeclaration(Read(entry))
                : null;

            if (declaration is null)
            {
                logger.LogWarning("NR SR public officials: no readable declaration for {UserId} ({Name}); listed without functions and skipped.", official.UserId, official.ListedName);
                continue;
            }

            // The list writes "Priezvisko, Meno".
            var comma = official.ListedName.IndexOf(',', StringComparison.Ordinal);
            var family = comma > 0 ? official.ListedName[..comma].Trim() : official.ListedName.Trim();
            var given = comma > 0 ? official.ListedName[(comma + 1)..].Trim() : string.Empty;
            var (titleBefore, titleAfter) = VnfHtmlParser.Titles(declaration.HeaderName);

            var note = declaration.Year is { } year
                ? $"Podľa oznámenia za rok {year}; zdroj neuvádza dátum začiatku ani konca funkcie."
                : "Podľa posledného oznámenia; zdroj neuvádza dátum začiatku ani konca funkcie.";

            var url = DeclarationUrl(official.UserId);
            var functions = new List<ParsedFunction>();
            var keys = new HashSet<string>(StringComparer.Ordinal);

            foreach (var declared in declaration.Functions)
            {
                var function = VnfFunctions.Classify(declared);

                // Keyed by the wording, so the same office stays the same function from
                // one declaration to the next and a reworded one ends and begins anew.
                var key = "fn:" + NameNormalizer.Normalize(function.Role + " " + function.Organisation).Trim();
                if (!keys.Add(key))
                {
                    continue;
                }

                functions.Add(new ParsedFunction(key, Country.SK, function.Category, function.Institution, function.Role, null, null, url)
                {
                    DateNote = note,
                });
            }

            if (functions.Count == 0)
            {
                logger.LogWarning("NR SR public officials: {UserId} ({Name}) declares no function; skipped.", official.UserId, official.ListedName);
                continue;
            }

            persons.Add(new ParsedPerson(official.UserId, given, family, titleBefore, titleAfter, null, null, url, functions));
        }

        return persons;
    }

    private string ListUrl() => $"{options.Value.BaseUrl}Default.aspx?sid=vnf/zoznam";

    private string DeclarationUrl(string userId) => $"{options.Value.BaseUrl}Default.aspx?sid=vnf/oznamenie&UserId={Uri.EscapeDataString(userId)}";

    private static string DeclarationEntry(string userId) => "oznamenie-" + CacheName(userId);

    /// <summary>The site's identifiers are ASCII logins ("Tomas.Abel", "BALOBORI"); anything else is escaped so it is a file name.</summary>
    private static string CacheName(string userId) => Uri.EscapeDataString(userId).Replace('%', '_') + ".html";

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
