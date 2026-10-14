using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Learning;
using SancMeld.Core.Search;
using SancMeld.Experiments.NameEmbeddings;

// Does a multilingual embedding model reach the pairs the trigram search cannot?
//
// 56.3 % of confirmed positive pairs are not string-related at all — a different script,
// a translation, an unrelated alias — and a character-level index structurally cannot
// reach them. An embedding model that maps "Міхаіл ГРЫБ" and "Michail GRIB" to nearby
// vectors might. This measures whether it does, on two ground truths (see Mode).
//
// What is measured, per case: where the confirmed listing lands when the query is
// searched against the target list — by trigram (the matcher as shipped), by embedding
// alone, and by a fusion of the two. Recall at k is the number that matters; a candidate
// generator that misses is unrecoverable downstream.
//
// What is not measured: precision. Both rankings return k candidates whatever happens, so
// the cost of the embedding is not more false positives on screen but the extra k rows a
// scorer has to look at. That is the right trade for this product — see README.md,
// "Recall beats precision".

// Numbers in the report are copied into README.md; they should not depend on the locale
// of whoever ran it.
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

// Names in the report are in whatever script the publisher used. Redirected to a file on
// Windows, the console otherwise writes them in the legacy code page and every accent
// comes out as a question mark.
Console.OutputEncoding = Encoding.UTF8;

RunSettings? parsed;

try
{
    parsed = RunSettings.Parse(args);
}
catch (Exception ex) when (ex is ArgumentException or FormatException or UriFormatException)
{
    Console.Error.WriteLine(ex.Message);
    Console.Error.WriteLine();
    Console.Error.WriteLine(RunSettings.Usage);
    return 2;
}

if (parsed is null)
{
    Console.Error.WriteLine(RunSettings.Usage);
    return 2;
}

var settings = parsed;

var configuration = new ConfigurationBuilder()
    .SetBasePath(AppContext.BaseDirectory)
    .AddJsonFile("appsettings.json")
    .AddEnvironmentVariables()
    .Build();

var connectionString = configuration.GetConnectionString("SancMeld")
    ?? throw new InvalidOperationException("ConnectionStrings:SancMeld is not configured.");

// The same services the server registers for this, and no more. Registered by hand rather
// than through AddSancMeld() so the tool does not carry ASP.NET and three connectors it
// never calls.
var services = new ServiceCollection();
services.AddOptions<SancMeldOptions>().Bind(configuration.GetSection(SancMeldOptions.SectionName));
services.AddDbContext<SancMeldDbContext>(o => o.UseSancMeld(connectionString));
services.AddSingleton(TimeProvider.System);
services.AddScoped<DesignationQueryService>();
services.AddScoped<IScoringStrategy, RulesScoringStrategy>();
services.AddScoped<XrefTrainingSeeder>();

await using var provider = services.BuildServiceProvider();
await using var scope = provider.CreateAsyncScope();

var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();
var queries = scope.ServiceProvider.GetRequiredService<DesignationQueryService>();
var seeder = scope.ServiceProvider.GetRequiredService<XrefTrainingSeeder>();
var matching = scope.ServiceProvider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Matching;

// The same filters the search applies — current revisions, weak aliases only when they
// are scored — so a name the embedding reaches is a name the search could have shown.
var includeWeak = matching.ScoreWeakAliases;

// ---------------------------------------------------------------------------------------
// 1. Ground truth and index, by mode. Cases are in a fixed order so --limit means the
//    same twice.
// ---------------------------------------------------------------------------------------

List<Case> cases;
List<NameRow> rows;
var skippedWithoutName = 0;

if (settings.Mode == Mode.Xref)
{
    var links = (await seeder.ResolvedLinksAsync())
        .Where(l => settings.TargetSource is null || l.TargetSourceCode == settings.TargetSource)
        .OrderBy(l => l.TargetSourceCode, StringComparer.Ordinal)
        .ThenBy(l => l.FromDesignationId)
        .ThenBy(l => l.ToDesignationId)
        .ToList();

    if (settings.Limit is { } limit)
    {
        links = links.Take(limit).ToList();
    }

    if (links.Count == 0)
    {
        Console.Error.WriteLine(
            "No resolved cross-references. Import the lists first — EU_FSF, UN_SC and FR_GELS — "
            + "or check --target against the source codes in sanctions_source.");
        return 1;
    }

    // The query is what the corpus seeder uses: the citing listing's primary name, or its
    // first name where no primary is marked. Not an alias — a person screening for this
    // subject would type the name they were given, not the fourth spelling on the list.
    cases = new List<Case>(links.Count);

    foreach (var link in links)
    {
        var subject = await queries.GetAsync(link.FromDesignationId);
        var query = subject?.Names.FirstOrDefault(n => n.Kind == NameType.Primary)?.Value
            ?? subject?.Names.FirstOrDefault()?.Value;

        if (subject is null || string.IsNullOrWhiteSpace(query))
        {
            skippedWithoutName++;
            continue;
        }

        cases.Add(new Case(link.FromDesignationId, link.ToDesignationId, subject.Source, link.TargetSourceCode, query));
    }

    var targetSources = cases.Select(c => c.ToSource).Distinct().ToList();

    rows = await db.Names
        .Where(n => n.Designation!.ValidTo == null)
        .Where(n => targetSources.Contains(n.Designation!.Source!.Code))
        .Where(n => includeWeak || !n.IsWeak)
        .Select(n => new NameRow(n.Designation!.Source!.Code, n.DesignationId, n.NameOriginal))
        .ToListAsync();
}
else
{
    // Every current name, then split by script in memory: the database has no filled
    // script column to filter on, and 50 000 short strings are nothing to hold.
    var names = await db.Names
        .Where(n => n.Designation!.ValidTo == null)
        .Where(n => settings.TargetSource == null || n.Designation!.Source!.Code == settings.TargetSource)
        .Where(n => includeWeak || !n.IsWeak)
        .Select(n => new { n.DesignationId, Source = n.Designation!.Source!.Code, n.NameOriginal, n.Kind })
        .ToListAsync();

    // The index is the Latin spellings only. Hiding the other script is the experiment:
    // a listing whose publisher recorded one script is exactly a listing whose other
    // script is not in the index.
    rows = names
        .Where(n => Script.IsLatin(n.NameOriginal))
        .Select(n => new NameRow(n.Source, n.DesignationId, n.NameOriginal))
        .ToList();

    // One case per listing that has both: the hidden spelling is the query, the listing
    // itself is the answer. The primary spelling when it is the non-Latin one — the EU
    // records 4 371 subjects that way — otherwise the first non-Latin alias.
    cases = names
        .GroupBy(n => (n.DesignationId, n.Source))
        .Where(g => g.Any(n => Script.IsLatin(n.NameOriginal)) && g.Any(n => !Script.IsLatin(n.NameOriginal)))
        .Select(g =>
        {
            var hidden = g.Where(n => !Script.IsLatin(n.NameOriginal))
                .OrderByDescending(n => n.Kind == NameType.Primary)
                .First();

            return new Case(g.Key.DesignationId, g.Key.DesignationId, g.Key.Source, g.Key.Source, hidden.NameOriginal);
        })
        .OrderBy(c => c.ToSource, StringComparer.Ordinal)
        .ThenBy(c => c.ToDesignationId)
        .ToList();

    if (settings.Limit is { } limit)
    {
        cases = cases.Take(limit).ToList();
    }

    if (cases.Count == 0)
    {
        Console.Error.WriteLine("No listing carries both a Latin and a non-Latin spelling. Import EU_FSF or UN_SC first.");
        return 1;
    }
}

var pairCounts = string.Join(", ", cases
    .GroupBy(c => c.Pair)
    .Select(g => $"{g.Key} {g.Count()}"));

Console.Error.WriteLine($"{settings.Mode}: {cases.Count} cases ({pairCounts}), {skippedWithoutName} skipped without a name");

// ---------------------------------------------------------------------------------------
// 2. The index.
// ---------------------------------------------------------------------------------------

using var embedder = new OllamaEmbedder(settings.Endpoint, settings.Model);
var cache = EmbeddingCache.Load(settings.CacheDirectory, settings.Model);
var cachedBefore = cache.Count;

// Synchronous on purpose: Progress<T> posts to the thread pool, and its callbacks would
// land after the WriteLine that is supposed to end the line.
var progress = new ConsoleProgress(p => Console.Error.Write($"\r  embedding {p.Done:N0} / {p.Total:N0}"));

var indexSources = rows.Select(r => r.SourceCode).Distinct().Order().ToList();
var indexing = Stopwatch.StartNew();
Console.Error.WriteLine($"{rows.Count:N0} names on {string.Join(", ", indexSources)}, model {settings.Model}");
var index = await NameIndex.BuildAsync(rows, settings.NamePrefix, embedder, cache, progress);
indexing.Stop();
Console.Error.WriteLine();

var queryVectors = await index.EmbedQueriesAsync([.. cases.Select(c => settings.QueryPrefix + c.Query)], progress);
Console.Error.WriteLine();

var embeddedNow = cache.Count - cachedBefore;

// ---------------------------------------------------------------------------------------
// 3. The measurement.
// ---------------------------------------------------------------------------------------

var k = settings.Candidates;
var results = new List<LinkResult>(cases.Count);
var skippedUnindexed = 0;
var trigramTime = new Stopwatch();
var vectorTime = new Stopwatch();

for (var i = 0; i < cases.Count; i++)
{
    var c = cases[i];

    trigramTime.Start();
    var hits = await queries.SearchAsync(c.Query, k, c.ToSource);
    trigramTime.Stop();

    if (settings.Mode == Mode.CrossScript)
    {
        // The hidden spelling is still in the database, and the search will find it —
        // which is the production behaviour, not the experiment. A hit reached through a
        // non-Latin spelling is one the Latin-only list could not have produced, so it
        // goes. A listing can match through a Latin alias as well and be dropped here
        // because its best spelling was the hidden one; against a query in another
        // script that Latin match is near zero anyway, so the trigram loses nothing it
        // would really have had.
        hits = hits.Where(h => Script.IsLatin(h.MatchedName)).ToList();
    }

    var trigramIds = hits.Select(h => h.Designation.Id).ToList();

    vectorTime.Start();
    var ranked = index.Rank(c.ToSource, queryVectors[i]);
    vectorTime.Stop();

    var rankedIds = ranked.Select(r => r.DesignationId).ToList();
    var embeddingRank = Metrics.RankOf(rankedIds, c.ToDesignationId);

    if (embeddingRank is null)
    {
        // Every spelling of the target is a weak alias, so neither search can see it.
        // Not a miss of either method; it is outside what the search is allowed to show.
        skippedUnindexed++;
        continue;
    }

    var fused = Fusion.ReciprocalRank(trigramIds, rankedIds.Take(k).ToList());
    var target = ranked[embeddingRank.Value - 1];

    results.Add(new LinkResult(
        c.FromSource,
        c.ToSource,
        c.FromDesignationId,
        c.ToDesignationId,
        c.Query,
        Metrics.RankOf(trigramIds, c.ToDesignationId),
        embeddingRank.Value,
        Metrics.RankOf(fused, c.ToDesignationId),
        target.Name,
        target.Score));

    if ((i + 1) % 100 == 0)
    {
        Console.Error.Write($"\r  searching {i + 1:N0} / {cases.Count:N0}");
    }
}

Console.Error.WriteLine();

// ---------------------------------------------------------------------------------------
// 4. The report. Markdown, so the table can go straight into README.md.
// ---------------------------------------------------------------------------------------

var ks = new[] { 1, 5, 10, k }.Where(x => x <= k).Distinct().Order().ToArray();
var report = new StringBuilder();

var embeddingRate = embeddedNow > 0
    ? $" ({embeddedNow / indexing.Elapsed.TotalSeconds:N1} names/s)"
    : " (all cached)";

report.AppendLine($"Mode {settings.Mode}, model `{settings.Model}` ({index.Dimensions} dims), k = {k}, {results.Count} cases");
report.AppendLine($"Index: {rows.Count:N0} names, {embeddedNow:N0} embedded this run in {indexing.Elapsed.TotalSeconds:N0} s{embeddingRate}");
report.AppendLine($"Per query: trigram {trigramTime.Elapsed.TotalMilliseconds / results.Count:N1} ms, "
    + $"vector search {vectorTime.Elapsed.TotalMilliseconds / results.Count:N1} ms");

if (skippedWithoutName + skippedUnindexed > 0)
{
    report.AppendLine($"Skipped: {skippedWithoutName} without a name, {skippedUnindexed} with only weak aliases");
}

report.AppendLine();

AppendTable(report, "All cases", results);

foreach (var group in results.GroupBy(r => r.Pair).OrderBy(g => g.Key, StringComparer.Ordinal))
{
    AppendTable(report, group.Key, group.ToList());
}

// The headline. These are the pairs the shipped matcher does not return at all — the only
// ones a second candidate generator could add.
var beyondReach = results.Where(r => r.TrigramRank is null || r.TrigramRank > k).ToList();
var reachedByEmbedding = beyondReach.Count(r => r.EmbeddingRank <= k);
var reachedByFusion = beyondReach.Count(r => r.FusedRank is { } f && f <= k);

report.AppendLine($"**Beyond trigram reach at k = {k}: {beyondReach.Count} of {results.Count} "
    + $"({Percent(beyondReach.Count, results.Count)})**");
report.AppendLine($"- embedding alone reaches {reachedByEmbedding} ({Percent(reachedByEmbedding, beyondReach.Count)})");
report.AppendLine($"- fusion reaches {reachedByFusion} ({Percent(reachedByFusion, beyondReach.Count)})");

// The other half of the LightGBM lesson: a ranking that finds new pairs by losing old ones
// is not an improvement. Fusion of two top-k lists is at most 2k long, so this can be
// non-zero when k embedding-only candidates outrank a trigram hit.
var lostByFusion = results.Count(r =>
    r.TrigramRank is { } t && t <= k && (r.FusedRank is null || r.FusedRank > k));

report.AppendLine($"- reached by trigram but pushed past k by fusion: {lostByFusion}");
report.AppendLine();

// Worth reading by eye: the pairs neither method reaches say what the model cannot do.
var unreached = results
    .Where(r => r.EmbeddingRank > k && (r.TrigramRank is null || r.TrigramRank > k))
    .Take(15)
    .ToList();

if (unreached.Count > 0)
{
    report.AppendLine("Unreached by either, first 15:");
    report.AppendLine();
    report.AppendLine("| query | best embedding name | score | embedding rank |");
    report.AppendLine("|---|---|---:|---:|");

    foreach (var r in unreached)
    {
        report.AppendLine($"| {r.Query} | {r.EmbeddingBestName} | {r.EmbeddingBestScore:N3} | {r.EmbeddingRank} |");
    }

    report.AppendLine();
}

Console.WriteLine(report);

// Every case with its ranks, for slicing the result in ways this report did not think of.
Directory.CreateDirectory(settings.ResultsDirectory);
var stamp = DateTimeOffset.Now.ToString("yyyyMMdd-HHmmss");
var safeModel = string.Concat(settings.Model.Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
var modeTag = settings.Mode == Mode.Xref ? "xref" : "cross-script";
var resultPath = Path.Combine(settings.ResultsDirectory, $"{safeModel}-{modeTag}-{stamp}.json");

var json = JsonSerializer.Serialize(
    new
    {
        settings,
        names = rows.Count,
        skippedWithoutName,
        skippedUnindexed,
        report = report.ToString(),
        links = results,
    },
    new JsonSerializerOptions { WriteIndented = true, Converters = { new JsonStringEnumConverter() } });

await File.WriteAllTextAsync(resultPath, json);
Console.Error.WriteLine($"Written {resultPath}");
return 0;

void AppendTable(StringBuilder sb, string title, IReadOnlyList<LinkResult> rs)
{
    sb.AppendLine($"### {title} ({rs.Count})");
    sb.AppendLine();
    sb.Append("| Ranking |");

    foreach (var kk in ks)
    {
        sb.Append($" recall@{kk} |");
    }

    sb.AppendLine(" MRR |");
    sb.Append("|---|");
    sb.Append(string.Concat(Enumerable.Repeat("---:|", ks.Length)));
    sb.AppendLine("---:|");

    AppendRow(sb, "`pg_trgm`", rs.Select(r => r.TrigramRank).ToList());
    AppendRow(sb, "embedding", rs.Select(r => (int?)r.EmbeddingRank).ToList());
    AppendRow(sb, "RRF fusion", rs.Select(r => r.FusedRank).ToList());
    sb.AppendLine();
}

void AppendRow(StringBuilder sb, string label, IReadOnlyList<int?> ranks)
{
    sb.Append($"| {label} |");

    foreach (var kk in ks)
    {
        sb.Append($" {Metrics.RecallAt(ranks, kk):P1} |");
    }

    sb.AppendLine($" {Metrics.MeanReciprocalRank(ranks, k):N3} |");
}

static string Percent(int part, int whole) => whole == 0 ? "n/a" : ((double)part / whole).ToString("P1");

/// <summary>One thing to find, and the listing that is the right answer.</summary>
sealed record Case(Guid FromDesignationId, Guid ToDesignationId, string FromSource, string ToSource, string Query)
{
    public string Pair => $"{FromSource}→{ToSource}";
}

/// <summary>IProgress that reports on the calling thread. See the comment where it is used.</summary>
sealed class ConsoleProgress(Action<(int Done, int Total)> report) : IProgress<(int Done, int Total)>
{
    public void Report((int Done, int Total) value) => report(value);
}
