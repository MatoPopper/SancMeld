using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using PepMeld.Core.Matching;

namespace PepMeld.Connectors.Wikidata;

/// <summary>A tie as Wikidata records it, one row of the family query.</summary>
internal sealed record WikidataTie(
    string PepItem,
    string PepLabel,
    DateOnly PepBirthDate,
    Country Country,
    FamilyRelation Relation,
    string RelativeItem,
    string RelativeLabel,
    DateOnly? RelativeBirthDate,
    bool Ended,
    bool RelativeDied);

public sealed record FamilySyncOutcome(ImportStatus Status, int Peps, int TiesSeen, int Inserted, int Updated, TimeSpan Duration, string? Error);

/// <summary>
/// Family ties from Wikidata for the holders of the positions PepMeld reads, and for the
/// members of both parliaments: spouse (P26), unmarried partner (P451), child (P40), father
/// and mother (P22, P25), sibling (P3373). Measured on 2026-10-20: 16 % of Czech senators,
/// 32 % of Czech and 16 % of Slovak government members have at least one, and 293 of the
/// 317 relatives come with a date of birth, which the agent's press research almost never
/// finds. A tie from here is level B: structured and cited, but editable by anyone.
///
/// Written into family_link beside the agent's ties: the same tie found by both is one row
/// with the evidence of both and the better level. Nothing is deleted; a tie Wikidata no
/// longer has keeps its last-found date.
/// </summary>
public sealed class WikidataFamilySync(
    IHttpClientFactory httpClients,
    IOptions<WikidataPositionsOptions> options,
    PepMeldDbContext db,
    TimeProvider clock,
    ILogger<WikidataFamilySync> logger)
{
    public const string SourceCode = "WIKIDATA_FAMILY";

    /// <summary>The parliaments, read here though not as positions: their members have list sources with dates of birth already.</summary>
    internal static readonly (string Item, Country Country)[] Parliaments = [("Q19803234", Country.CZ), ("Q19967563", Country.SK)];

    private static readonly (string Property, FamilyRelation Relation)[] Properties =
    [
        ("P26", FamilyRelation.Spouse),
        ("P451", FamilyRelation.Partner),
        ("P40", FamilyRelation.Child),
        ("P22", FamilyRelation.Parent),
        ("P25", FamilyRelation.Parent),
        ("P3373", FamilyRelation.Sibling),
    ];

    public async Task<FamilySyncOutcome> SyncAsync(CancellationToken ct)
    {
        var started = clock.GetUtcNow();
        var sync = new FamilySync { Id = Guid.CreateVersion7(), Source = SourceCode, StartedAt = started, Status = ImportStatus.Running };
        db.FamilySyncs.Add(sync);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            var ties = new List<WikidataTie>();
            var failed = new List<string>();

            // One position at a time, and one that the query service cannot answer (504 after
            // its minute) is tried once more and then left for tomorrow: the others still count.
            foreach (var (position, country) in Positions())
            {
                var languages = country == Country.SK ? "sk,cs,en" : "cs,sk,en";
                string? json = null;

                for (var attempt = 1; attempt <= 2 && json is null; attempt++)
                {
                    var watch = System.Diagnostics.Stopwatch.StartNew();
                    try
                    {
                        json = await QueryAsync(position, languages, ct).ConfigureAwait(false);
                        logger.LogInformation("Wikidata family {Position}: {Seconds:N1} s.", position.Key, watch.Elapsed.TotalSeconds);
                    }
                    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
                    {
                        logger.LogWarning("Wikidata family {Position}: attempt {Attempt} failed after {Seconds:N0} s: {Message}", position.Key, attempt, watch.Elapsed.TotalSeconds, ex.Message);
                    }

                    await Task.Delay(TimeSpan.FromMilliseconds(options.Value.DelayMilliseconds), clock, ct).ConfigureAwait(false);
                }

                if (json is null)
                {
                    failed.Add(position.Key);
                    continue;
                }

                ties.AddRange(Parse(json, country));
            }

            if (failed.Count > 0)
            {
                sync.Error = $"Not answered: {string.Join(", ", failed)}";
            }

            var unique = ties
                .GroupBy(t => (t.PepItem, t.Relation, t.RelativeItem))
                .Select(g => g.OrderByDescending(t => t.RelativeBirthDate is not null).First())
                .ToList();

            var (inserted, updated) = await WriteAsync(unique, ct).ConfigureAwait(false);

            sync.Status = ImportStatus.Success;
            sync.Peps = unique.Select(t => t.PepItem).Distinct().Count();
            sync.TiesSeen = unique.Count;
            sync.Inserted = inserted;
            sync.Updated = updated;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogError(ex, "Wikidata family sync failed.");
            db.ChangeTracker.Clear();
            db.FamilySyncs.Attach(sync);
            sync.Status = ImportStatus.Failed;
            sync.Error = ex.Message;
        }

        sync.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Wikidata family sync: {Status}, {Peps} PEPs, {Ties} ties, {Inserted} new, {Updated} updated.", sync.Status, sync.Peps, sync.TiesSeen, sync.Inserted, sync.Updated);
        return new FamilySyncOutcome(sync.Status, sync.Peps, sync.TiesSeen, sync.Inserted, sync.Updated, sync.FinishedAt.Value - started, sync.Error);
    }

    private IEnumerable<(WikidataPosition Position, Country Country)> Positions()
    {
        foreach (var (code, positions) in options.Value.Sources)
        {
            var country = code == WikidataPositionsConnector.SlovakCode ? Country.SK : Country.CZ;
            foreach (var position in positions)
            {
                yield return (position, country);
            }
        }

        foreach (var (item, country) in Parliaments)
        {
            yield return (new WikidataPosition { Item = item, Institution = "parlament", Category = PepCategory.MemberOfParliament }, country);
        }
    }

    private async Task<string> QueryAsync(WikidataPosition position, string languages, CancellationToken ct)
    {
        // A family of positions (every minister of a country) is too much for one query: the
        // subclass walk and the family joins together ran past the service's minute for
        // Czechia. The positions are listed first, cheaply, and named in the family query.
        if (position.Jurisdiction is { Length: > 0 } || position.IncludeSubclasses)
        {
            var listing = await SendAsync(PositionsQuery(position), ct).ConfigureAwait(false);
            using var document = JsonDocument.Parse(listing);
            var items = SparqlResults.Rows(document.RootElement).Select(r => SparqlResults.EntityId(r, "pos")).OfType<string>().Distinct().ToList();
            return await SendAsync(Query(position, languages, items), ct).ConfigureAwait(false);
        }

        return await SendAsync(Query(position, languages), ct).ConfigureAwait(false);
    }

    internal static string PositionsQuery(WikidataPosition position) =>
        position.Jurisdiction is { Length: > 0 } jurisdiction
            ? $"SELECT ?pos WHERE {{ ?pos wdt:P279* wd:{position.Item} . ?pos wdt:P1001 wd:{jurisdiction} . }}"
            : $"SELECT ?pos WHERE {{ ?pos wdt:P279* wd:{position.Item} . }}";

    private async Task<string> SendAsync(string query, CancellationToken ct)
    {
        var url = $"{options.Value.SparqlEndpoint}?query={Uri.EscapeDataString(query)}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/sparql-results+json");
        using var response = await httpClients.CreateClient(WikidataPositionsConnector.HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Holders of the position with a birthday known to the day, and each of their relatives
    /// by the six properties, with the end of the tie (P582, a divorce) and the relative's
    /// own date of birth (to the day only) and death.
    /// </summary>
    internal static string Query(WikidataPosition position, string languages, IReadOnlyList<string>? items = null)
    {
        var positions = items is { Count: > 0 }
            ? $"VALUES ?pos {{ {string.Join(' ', items.Select(i => "wd:" + i))} }}"
            : position.Jurisdiction is { Length: > 0 } jurisdiction
            ? $"?pos wdt:P279* wd:{position.Item} . ?pos wdt:P1001 wd:{jurisdiction} ."
            : position.IncludeSubclasses
                ? $"?pos wdt:P279* wd:{position.Item} ."
                : $"BIND(wd:{position.Item} AS ?pos)";
        var citizenship = position.Citizenship is { Length: > 0 } c ? $"?p wdt:P27 wd:{c} ." : string.Empty;
        var pairs = string.Join(' ', Properties.Select(p => $"(p:{p.Property} ps:{p.Property} \"{p.Property}\")"));

        return $$"""
            SELECT DISTINCT ?p ?pLabel ?dob ?prop ?rel ?relLabel ?relDob ?end ?relDeath WHERE {
              {{positions}}
              ?p p:P39/ps:P39 ?pos .
              {{citizenship}}
              ?p p:P569/psv:P569 ?dobNode . ?dobNode wikibase:timeValue ?dob ; wikibase:timePrecision 11 .
              VALUES (?statement ?value ?prop) { {{pairs}} }
              ?p ?statement ?st . ?st ?value ?rel . FILTER(isIRI(?rel))
              OPTIONAL { ?st pq:P582 ?end }
              OPTIONAL { ?rel p:P569/psv:P569 ?relNode . ?relNode wikibase:timeValue ?relDob ; wikibase:timePrecision 11 . }
              OPTIONAL { ?rel wdt:P570 ?relDeath }
              SERVICE wikibase:label { bd:serviceParam wikibase:language "{{languages}}". }
            }
            """;
    }

    internal static IEnumerable<WikidataTie> Parse(string json, Country country)
    {
        using var document = JsonDocument.Parse(json);

        foreach (var row in SparqlResults.Rows(document.RootElement).ToList())
        {
            var pep = SparqlResults.EntityId(row, "p");
            var rel = SparqlResults.EntityId(row, "rel");
            var pepLabel = SparqlResults.Value(row, "pLabel");
            var relLabel = SparqlResults.Value(row, "relLabel");
            var born = SparqlResults.Date(row, "dob");
            var property = SparqlResults.Value(row, "prop");

            // An item without a label in any of the languages comes back as its own id: no name to match on.
            if (pep is null || rel is null || born is null || pepLabel is null || relLabel is null || relLabel == rel || property is null)
            {
                continue;
            }

            var relation = Properties.FirstOrDefault(p => p.Property == property).Relation;

            yield return new WikidataTie(
                pep, pepLabel, born.Value, country, relation, rel, relLabel,
                SparqlResults.Date(row, "relDob"),
                SparqlResults.Value(row, "end") is not null,
                SparqlResults.Value(row, "relDeath") is not null);
        }
    }

    internal async Task<(int Inserted, int Updated)> WriteAsync(IReadOnlyList<WikidataTie> ties, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var today = DateOnly.FromDateTime(now.UtcDateTime);
        int inserted = 0, updated = 0;

        var existing = await db.FamilyLinks.ToListAsync(ct).ConfigureAwait(false);
        var byPep = existing.GroupBy(l => (l.PepNameNormalized, l.PepBirthDate, l.Relation)).ToDictionary(g => g.Key, g => g.ToList());

        foreach (var tie in ties)
        {
            var pepNormalized = PersonNames.Normalize(tie.PepLabel);
            var nameNormalized = PersonNames.Normalize(tie.RelativeLabel);
            if (pepNormalized.Length == 0 || nameNormalized.Length == 0)
            {
                continue;
            }

            // A minor is not kept (see FamilyLink).
            if (tie.RelativeBirthDate is { } relBorn && relBorn > today.AddYears(-18))
            {
                continue;
            }

            var key = (pepNormalized, tie.PepBirthDate, tie.Relation);
            if (!byPep.TryGetValue(key, out var same))
            {
                byPep[key] = same = [];
            }

            var status = tie.RelativeDied ? "deceased" : tie.Ended ? "former" : "current";
            var evidence = new List<Evidence>
            {
                new($"https://www.wikidata.org/wiki/{tie.PepItem}", now, $"{tie.PepLabel}: {Property(tie.Relation)} {tie.RelativeLabel} ({tie.RelativeItem})"),
                new($"https://www.wikidata.org/wiki/{tie.RelativeItem}", now, null),
            };

            var link = FamilyLinkMerge.FindExisting(same, nameNormalized);
            if (link is null)
            {
                link = new FamilyLink
                {
                    Id = Guid.CreateVersion7(),
                    PepName = tie.PepLabel,
                    PepNameNormalized = pepNormalized,
                    PepBirthDate = tie.PepBirthDate,
                    PepCountry = tie.Country,
                    Relation = tie.Relation,
                    RelativeName = tie.RelativeLabel,
                    RelativeNameNormalized = nameNormalized,
                    Status = status,
                    Confidence = "high",
                    EvidenceJson = "[]",
                    FoundBy = FamilyFoundBy.Wikidata,
                    Level = EvidenceLevel.B,
                    ReviewStatus = FamilyReviewStatus.Candidate,
                    CreatedAt = now,
                };
                db.FamilyLinks.Add(link);
                same.Add(link);
                inserted++;
            }
            else
            {
                updated++;
            }

            // Wikidata's name and date are the register-like form; the agent's stay as evidence.
            link.RelativeName = tie.RelativeLabel;
            link.RelativeNameNormalized = nameNormalized;
            link.RelativeBirthDate = tie.RelativeBirthDate ?? link.RelativeBirthDate;
            link.RelativeBirthYear = link.RelativeBirthDate?.Year ?? link.RelativeBirthYear;
            link.Status = status == "current" && link.Status is "former" or "deceased" ? link.Status : status;
            link.Level = FamilyLinkMerge.Best(link.Level, EvidenceLevel.B);
            link.EvidenceJson = FamilyLinkMerge.MergeEvidence(link.EvidenceJson, evidence, incomingIsWikidata: true);
            link.UpdatedAt = now;
            link.LastFoundAt = now;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (inserted, updated);
    }

    private static string Property(FamilyRelation relation) => relation switch
    {
        FamilyRelation.Spouse => "manžel/manželka (P26)",
        FamilyRelation.Partner => "partner (P451)",
        FamilyRelation.Child => "dieťa (P40)",
        FamilyRelation.Parent => "rodič (P22/P25)",
        FamilyRelation.Sibling => "súrodenec (P3373)",
        _ => relation.ToString(),
    };
}
