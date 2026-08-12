using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Search;

namespace SancMeld.Core.Learning;

/// <summary>
/// Builds labelled training pairs from the cross-references publishers declare between
/// their lists.
///
/// The corpus has a cold start problem that adjudications alone cannot fix: an analyst
/// manages tens of decisions a day, so waiting for enough of them means waiting months.
/// Publishers have already done part of the work — when the EU cites a UN reference
/// number, two independent bodies have said "this is the same subject", each having
/// transliterated the name their own way. Those are verified positives, available today.
///
/// Why these pairs and not the far more numerous alias pairs inside one listing
/// (234 617 of them, ADR-0001 §3): aliases teach spelling only. Within one subject the
/// date of birth, the nationality and the subject type are identical by construction, so
/// a model trained on them learns nothing about qualifiers and is overconfident about
/// what it has never seen disagree. Cross-source pairs are the opposite — the qualifiers
/// come from two publishers who genuinely differ, which is the only place the disagreement
/// a model must weigh actually occurs.
///
/// The result is written with <c>Origin = PUBLIC_LIST_XREF</c> and carries no personal
/// data — feature vectors and labels, nothing that names anybody.
/// </summary>
public sealed class XrefTrainingSeeder(
    SancMeldDbContext db,
    DesignationQueryService queries,
    IScoringStrategy scoring,
    IOptions<SancMeldOptions> options,
    TimeProvider clock)
{
    public const string Origin = "PUBLIC_LIST_XREF";

    /// <summary>Matches <see cref="ScreeningService"/> — enum members as text, for the same reason.</summary>
    private static readonly JsonSerializerOptions FeatureFormat = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<XrefSeedResult> SeedAsync(
        bool replace = false,
        int candidatesPerPair = 25,
        CancellationToken ct = default)
    {
        var existing = await db.TrainingSamples.CountAsync(s => s.Origin == Origin, ct).ConfigureAwait(false);

        if (existing > 0 && !replace)
        {
            // Running twice would double every pair, and a corpus with duplicates weights
            // whatever was seeded twice. Refusing is recoverable; silently appending is not.
            return new XrefSeedResult { AlreadyPresent = existing };
        }

        var algorithm = await db.MatchAlgorithms
            .Where(a => a.IsActive && a.Code == scoring.AlgorithmCode)
            .OrderByDescending(a => a.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No active row in match_algorithm for code '{scoring.AlgorithmCode}'. Every sample names "
                + "the feature set that produced it, so a strategy without a row cannot seed one.");

        if (existing > 0)
        {
            await db.TrainingSamples.Where(s => s.Origin == Origin).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        }

        var links = await ResolvedLinksAsync(ct).ConfigureAwait(false);
        var threshold = options.Value.Matching.MatchThreshold;
        var now = clock.GetUtcNow();
        var period = new DateOnly(now.Year, now.Month, 1);

        var result = new XrefSeedResult { LinksResolved = links.Count };
        var samples = new List<TrainingSample>();

        foreach (var link in links)
        {
            var subject = await queries.GetAsync(link.FromDesignationId, ct).ConfigureAwait(false);
            var query = QueryFrom(subject);

            if (query is null)
            {
                // No name to search with. Nothing to learn from and nothing to record.
                result.SkippedWithoutName++;
                continue;
            }

            var candidates = await queries
                .SearchAsync(query.Name, candidatesPerPair, link.TargetSourceCode, ct)
                .ConfigureAwait(false);

            var confirmed = candidates.FirstOrDefault(c => c.Designation.Id == link.ToDesignationId);

            if (confirmed is null)
            {
                // The interesting half. Trigram search cannot reach these — Ch'o'l Cho ↔
                // CHO IL U is the same person written by two publishers with no shared
                // substring — and leaving them out would train a model only on the pairs
                // the existing matcher already finds.
                var best = await queries
                    .BestNameMatchAsync(link.ToDesignationId, query.Name, ct)
                    .ConfigureAwait(false);

                var target = await queries.GetAsync(link.ToDesignationId, ct).ConfigureAwait(false);

                if (target is null || best is not { } scored)
                {
                    result.SkippedUnresolvable++;
                    continue;
                }

                confirmed = new SearchHit(target, scored.Name, scored.Score);
                candidates = [.. candidates, confirmed];
                result.PositivesBelowSearchReach++;
            }

            foreach (var candidate in candidates)
            {
                var isPositive = candidate.Designation.Id == link.ToDesignationId;

                // Identifiers are deliberately not supplied. A matching passport settles
                // the question before any model is asked (ADR-0001 §5), so a pair carrying
                // one teaches nothing about the case a model exists for.
                var explanation = ScreeningService.Explain(query, candidate, identifierMatch: false);
                var score = scoring.Score(explanation);

                samples.Add(new TrainingSample
                {
                    Features = JsonSerializer.Serialize(explanation, FeatureFormat),
                    Label = isPositive,

                    SourceCode = candidate.Designation.Source,
                    LegalEffect = candidate.Designation.LegalEffect,
                    SubjectType = candidate.Designation.SubjectType,

                    // Same meaning as on an adjudicated sample: the matcher would not have
                    // shown this one. Here it marks the pairs a model has to be measured
                    // on rather than the pairs it finds easy.
                    WasExploration = score < threshold,
                    ReasonCode = null,

                    FeatureSetVersion = algorithm.FeatureSetVersion,
                    NameNormVersion = algorithm.NameNormVersion,
                    PeriodMonth = period,
                    Origin = Origin,
                });

                if (isPositive)
                {
                    result.Positives++;
                }
                else
                {
                    result.Negatives++;
                }
            }
        }

        db.TrainingSamples.AddRange(samples);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        result.Replaced = existing;
        return result;
    }

    /// <summary>
    /// Cross-references whose target actually exists on the list it points at.
    ///
    /// Publishers get these wrong — the EU cites UN entries that were never published and
    /// writes LYi.28 where the UN has LYi.028. Those are the publisher's errors and are
    /// left alone rather than repaired; here they simply produce no pair.
    ///
    /// The join is on <see cref="Designation.ExternalId"/>, not the record key: the UN
    /// keys its records by a numeric DATAID and publishes the QDi./KPi. reference
    /// separately, which is the identifier everyone else cites.
    /// </summary>
    private async Task<IReadOnlyList<ResolvedLink>> ResolvedLinksAsync(CancellationToken ct) =>
        await (from xref in db.CrossReferences
               join from_ in db.Designations on xref.DesignationId equals from_.Id
               join to in db.Designations on xref.TargetExternalId equals to.ExternalId
               join target in db.Sources on to.SourceId equals target.Id
               where from_.ValidTo == null
                     && to.ValidTo == null
                     && xref.TargetSourceCode != null
                     && target.Code == xref.TargetSourceCode
               select new ResolvedLink(from_.Id, to.Id, target.Code))
            .Distinct()
            .ToListAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Turns one listing into the query a person screening for that subject would type.
    ///
    /// Only what a caller plausibly knows: a name, at most one year of birth, at most one
    /// nationality. Listings routinely carry ten dates of birth, and feeding all of them
    /// would build a query no real caller could make.
    /// </summary>
    private static ScreeningQuery? QueryFrom(DesignationView? subject)
    {
        var name = subject?.Names.FirstOrDefault(n => n.Kind == NameType.Primary)?.Value
            ?? subject?.Names.FirstOrDefault()?.Value;

        if (subject is null || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        // A stated date is what a caller would have off a document; a range or a circa is
        // the publisher hedging, and picking one of those as if it were stated would put
        // a precision into the pair that never existed. Both are still usable — they are
        // preferred second, not discarded.
        var birth = subject.Births.FirstOrDefault(b =>
                b.Year is not null && b.Precision is DatePrecision.Day or DatePrecision.Month or DatePrecision.Year)
            ?? subject.Births.FirstOrDefault(b => b.Year is not null);

        return new ScreeningQuery
        {
            Name = name,
            SubjectType = subject.SubjectType == SubjectType.Unknown ? null : subject.SubjectType,
            BirthYear = birth?.Year,
            CountryIso2 = subject.Nationalities.FirstOrDefault(n => n.CountryIso2 is not null)?.CountryIso2,
            Identifiers = [],
        };
    }

    private sealed record ResolvedLink(Guid FromDesignationId, Guid ToDesignationId, string TargetSourceCode);
}

public sealed class XrefSeedResult
{
    /// <summary>Non-zero when the seed refused to run because a corpus was already there.</summary>
    public int AlreadyPresent { get; set; }

    public int Replaced { get; set; }
    public int LinksResolved { get; set; }
    public int Positives { get; set; }
    public int Negatives { get; set; }

    /// <summary>
    /// Confirmed pairs the trigram search does not return at all. Worth watching: these
    /// are the matches the current matcher misses, so they are what a model would have to
    /// improve on.
    /// </summary>
    public int PositivesBelowSearchReach { get; set; }

    public int SkippedWithoutName { get; set; }
    public int SkippedUnresolvable { get; set; }

    public int Written => Positives + Negatives;
}
