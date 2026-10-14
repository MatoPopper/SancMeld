using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Embeddings;
using SancMeld.Core.Matching;

namespace SancMeld.Core.Search;

/// <summary>
/// Screening a name against the aggregated list.
///
/// The layers run cheapest and most certain first, as ADR-0001 describes:
///
///   1. deterministic — an identifier that matches exactly settles it
///   2. candidate generation — pg_trgm, deliberately wide, tuned for recall
///   3. qualifiers — birth year, country, subject type, each with "unknown" as a
///      first-class answer rather than a silent zero
///   4. ranking, and a record of every candidate considered
///
/// There is no model here yet, and the reason is measured rather than assumed: on the EU
/// list, plain trigram ranking beat a trained LightGBM ranker on names (recall@1 68 % vs
/// 36 %, recall@10 100 % vs 87 %). What a model can plausibly improve is step 3, and that
/// cannot be trained until adjudications exist. So this class computes and stores exactly
/// the feature vector such a model would consume — every screening decision made now is a
/// training example later.
/// </summary>
public sealed class ScreeningService(
    SancMeldDbContext db,
    DesignationQueryService queries,
    IScoringStrategy scoring,
    SemanticCandidateSource semantic,
    IOptions<SancMeldOptions> options,
    TimeProvider clock)
{
    /// <summary>Circa dates are approximations; the publisher says so, so allow a year either way.</summary>
    private const short CircaTolerance = 1;

    /// <summary>
    /// Enum members as text. These rows are training data with a long life: a numeric
    /// enum means inserting a member silently rewrites what every historical row said.
    /// </summary>
    private static readonly JsonSerializerOptions FeatureFormat = new()
    {
        Converters = { new JsonStringEnumConverter() },
    };

    public async Task<ScreeningResult> ScreenAsync(ScreeningQuery query, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        var started = Stopwatch.StartNew();
        var matching = options.Value.Matching;
        var normalized = NameNormalizer.Normalize(query.Name);

        var algorithmId = await CurrentAlgorithmIdAsync(ct).ConfigureAwait(false);
        var identifiers = query.Identifiers
            .Select(IdentifierNormalizer.Normalize)
            .Where(i => i.Length > 0)
            .ToList();

        // Deterministic first. An exact identifier match is not a similarity question, and
        // it finds subjects whose name was transliterated beyond any trigram's reach.
        var byIdentifier = identifiers.Count == 0
            ? []
            : await db.Identifiers
                .Where(i => i.Designation!.ValidTo == null && identifiers.Contains(i.ValueNormalized))
                .Select(i => i.DesignationId)
                .Distinct()
                .ToListAsync(ct)
                .ConfigureAwait(false);

        var candidates = await queries
            .SearchAsync(query.Name, matching.MaxCandidates, ct: ct)
            .ConfigureAwait(false);

        // Identifier matches that the name search missed entirely still belong in the
        // result — that is the point of running the deterministic layer first.
        var missing = byIdentifier.Except(candidates.Select(c => c.Designation.Id)).ToList();
        if (missing.Count > 0)
        {
            var extra = await DesignationQueryService
                .Hydrate(queries.CurrentRevisions().Where(d => missing.Contains(d.Id)))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            candidates = [.. candidates, .. extra.Select(d => new SearchHit(d, d.Names.FirstOrDefault()?.Value ?? string.Empty, 0))];
        }

        // The second candidate generator. Null whenever it has nothing to say — off, not
        // running, no vectors yet — and the screening is then exactly what it was before
        // one existed. In shadow mode its findings are recorded and not shown.
        var semanticRanking = await semantic.RankAsync(query.Name, ct).ConfigureAwait(false);
        var semanticTop = semanticRanking is null
            ? new HashSet<Guid>()
            : semanticRanking.Ranked.Take(options.Value.Embeddings.Candidates).Select(h => h.DesignationId).ToHashSet();

        var identifierHits = byIdentifier.ToHashSet();
        var hits = new List<ScreeningHit>(candidates.Count);

        foreach (var candidate in candidates)
        {
            var id = candidate.Designation.Id;
            var explanation = Explain(query, candidate, identifierHits.Contains(id));
            var score = scoring.Score(explanation);

            hits.Add(new ScreeningHit
            {
                Designation = candidate.Designation,
                MatchedName = candidate.MatchedName,
                Score = score,
                AboveThreshold = score >= matching.MatchThreshold,
                Explanation = explanation,
                RetrievedBy = missing.Contains(id) ? CandidateOrigin.Identifier
                    : semanticTop.Contains(id) ? CandidateOrigin.Both
                    : CandidateOrigin.Trigram,
                SemanticScore = semanticRanking?.Of(id)?.Score,
            });
        }

        hits = [.. hits.OrderByDescending(h => h.Score).ThenBy(h => h.Designation.Id)];

        var shadow = semanticRanking is null
            ? []
            : await ShadowHitsAsync(query, semanticRanking, semanticTop.Except(hits.Select(h => h.Designation.Id)), ct)
                .ConfigureAwait(false);

        var requestId = await RecordAsync(query, normalized, hits, shadow, algorithmId, matching, started, ct)
            .ConfigureAwait(false);

        return new ScreeningResult
        {
            RequestId = requestId,
            Query = query.Name,
            Threshold = matching.MatchThreshold,
            CandidatesConsidered = hits.Count,
            Hits = hits,
        };
    }

    /// <summary>
    /// The listings only the embedding reached, scored as the rules would score them.
    ///
    /// Their name score is the trigram's own answer below its threshold, from
    /// <see cref="DesignationQueryService.BestNameMatchAsync"/>, so the feature vector
    /// stored for them is the one a live screening would have produced had the trigram
    /// returned them — comparable row for row with everything else in the table. The
    /// rules will put nearly all of them under the threshold, which is the point: the
    /// shadow period asks how often that is wrong.
    /// </summary>
    private async Task<List<ScreeningHit>> ShadowHitsAsync(
        ScreeningQuery query,
        SemanticRanking ranking,
        IEnumerable<Guid> designationIds,
        CancellationToken ct)
    {
        var ids = designationIds.ToList();
        if (ids.Count == 0)
        {
            return [];
        }

        var designations = await DesignationQueryService
            .Hydrate(queries.CurrentRevisions().Where(d => ids.Contains(d.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var shadow = new List<ScreeningHit>(designations.Count);

        foreach (var designation in designations)
        {
            var semanticHit = ranking.Of(designation.Id)!;
            var best = await queries.BestNameMatchAsync(designation.Id, query.Name, ct).ConfigureAwait(false);
            var candidate = new SearchHit(designation, semanticHit.Name, best?.Score ?? 0);
            var explanation = Explain(query, candidate, identifierMatch: false);

            shadow.Add(new ScreeningHit
            {
                Designation = designation,
                MatchedName = semanticHit.Name,
                Score = scoring.Score(explanation),
                AboveThreshold = false,
                Explanation = explanation,
                RetrievedBy = CandidateOrigin.Semantic,
                SemanticScore = semanticHit.Score,
            });
        }

        return [.. shadow.OrderByDescending(h => h.SemanticScore).ThenBy(h => h.Designation.Id)];
    }

    /// <summary>
    /// The feature vector for one query-candidate pair.
    ///
    /// Public because training pairs built from cross-source references have to produce
    /// <em>the same</em> vector as a live screening does. Two producers of the same corpus
    /// computing features separately is how a model ends up trained on one distribution
    /// and run on another, and nothing in the data would show it.
    /// </summary>
    public static MatchExplanation Explain(ScreeningQuery query, SearchHit candidate, bool identifierMatch)
    {
        var designation = candidate.Designation;
        var notes = new List<string>();

        var birth = CompareBirthYear(query.BirthYear, designation.Births, notes);
        var country = CompareCountry(query.CountryIso2, designation, notes);
        var subjectType = CompareSubjectType(query.SubjectType, designation.SubjectType);

        if (identifierMatch)
        {
            notes.Add("An identifier on this listing matches the one supplied.");
        }

        if (designation.Status == DesignationStatus.PossiblyMissing)
        {
            notes.Add("This listing was absent from the publisher's most recent release and is under review.");
        }

        if (designation.LegalEffect == LegalEffect.Advisory)
        {
            notes.Add("Advisory source: a red flag, not an EU obligation.");
        }

        return new MatchExplanation
        {
            NameScore = candidate.NameScore,
            IdentifierMatch = identifierMatch,
            BirthYear = birth,
            Country = country,
            SubjectType = subjectType,
            Notes = notes,
        };
    }

    private static QualifierState CompareBirthYear(short? queried, IReadOnlyList<BirthView> births, List<string> notes)
    {
        if (queried is not { } year)
        {
            return QualifierState.NotProvided;
        }

        var dated = births.Where(b => b.Year is not null || b.YearFrom is not null).ToList();
        if (dated.Count == 0)
        {
            // The single most common shape in this data. Saying "contradicts" here would
            // bury every subject the publishers know least about.
            notes.Add("This listing carries no date of birth, so the year could not be checked.");
            return QualifierState.UnknownOnRecord;
        }

        foreach (var birth in dated)
        {
            if (birth.YearFrom is { } from && birth.YearTo is { } to && year >= from && year <= to)
            {
                return QualifierState.Agrees;
            }

            if (birth.Year is not { } recorded)
            {
                continue;
            }

            var tolerance = birth.Precision == DatePrecision.Circa ? CircaTolerance : (short)0;
            if (Math.Abs(recorded - year) <= tolerance)
            {
                return QualifierState.Agrees;
            }
        }

        // Several listings routinely carry several conflicting dates for one subject, so
        // "contradicts" means none of them matched, not that one did not.
        notes.Add(dated.Count == 1
            ? "The recorded year of birth differs from the one supplied."
            : $"None of the {dated.Count} recorded years of birth matches the one supplied.");

        return QualifierState.Contradicts;
    }

    private static QualifierState CompareCountry(string? queried, DesignationView designation, List<string> notes)
    {
        if (string.IsNullOrWhiteSpace(queried))
        {
            return QualifierState.NotProvided;
        }

        // Nationality only. An address abroad says where someone was, not who they are,
        // and treating it as a contradiction would penalise exactly the mobile subjects
        // sanctions lists are most concerned with.
        var nationalities = designation.Nationalities
            .Select(n => n.CountryIso2)
            .Where(c => c is not null)
            .ToList();

        if (nationalities.Count == 0)
        {
            notes.Add("This listing records no nationality, so the country could not be checked.");
            return QualifierState.UnknownOnRecord;
        }

        return nationalities.Any(c => string.Equals(c, queried, StringComparison.OrdinalIgnoreCase))
            ? QualifierState.Agrees
            : QualifierState.Contradicts;
    }

    private static QualifierState CompareSubjectType(SubjectType? queried, SubjectType recorded)
    {
        if (queried is not { } wanted)
        {
            return QualifierState.NotProvided;
        }

        if (recorded == SubjectType.Unknown)
        {
            return QualifierState.UnknownOnRecord;
        }

        return recorded == wanted ? QualifierState.Agrees : QualifierState.Contradicts;
    }

    /// <summary>
    /// Stores the query, every candidate and its feature vector.
    ///
    /// This is the audit trail — "why did we clear this payment in March" is answerable
    /// only if the candidates and the threshold of that day were kept — and it is
    /// simultaneously the training set that step 3 will eventually be learned from.
    /// </summary>
    private async Task<Guid> RecordAsync(
        ScreeningQuery query,
        string normalized,
        List<ScreeningHit> hits,
        List<ScreeningHit> shadow,
        Guid algorithmId,
        MatchingOptions matching,
        Stopwatch started,
        CancellationToken ct)
    {
        var request = new ScreeningRequest
        {
            Id = Guid.CreateVersion7(),
            QueryOriginal = query.Name,
            QueryNormalized = normalized,
            QuerySubjectType = query.SubjectType,
            QueryBirthYear = query.BirthYear,
            QueryCountryIso2 = query.CountryIso2,
            QueryIdentifiers = query.Identifiers.Count == 0 ? null : JsonSerializer.Serialize(query.Identifiers),
            AlgorithmId = algorithmId,
            ThresholdUsed = (decimal)matching.MatchThreshold,
            RequestedAt = clock.GetUtcNow(),
            RequestedBy = query.RequestedBy,
            Channel = query.Channel ?? "API",
            CandidateCount = hits.Count,
        };

        var recorded = hits.Concat(shadow).ToList();

        var snapshotIds = await db.Designations
            .Where(d => recorded.Select(h => h.Designation.Id).Contains(d.Id))
            .Select(d => new { d.Id, d.SnapshotId })
            .ToDictionaryAsync(x => x.Id, x => x.SnapshotId, ct)
            .ConfigureAwait(false);

        // Shown candidates first, in their order; then the shadow ones, which were never
        // shown and rank after everything that was. retrieved_by tells them apart.
        var rank = 0;
        foreach (var hit in recorded)
        {
            rank++;

            request.Candidates.Add(new ScreeningCandidate
            {
                DesignationId = hit.Designation.Id,
                SnapshotId = snapshotIds[hit.Designation.Id],
                Rank = rank,
                Score = (decimal)hit.Score,
                NameScore = (decimal)hit.Explanation.NameScore,
                AboveThreshold = hit.AboveThreshold,

                // Kept deliberately: a model trained only on what was shown learns only
                // what was already believed.
                IsExploration = !hit.AboveThreshold,
                Features = JsonSerializer.Serialize(hit.Explanation, FeatureFormat),
                AlgorithmId = algorithmId,
                CreatedAt = clock.GetUtcNow(),
                RetrievedBy = hit.RetrievedBy,
                SemanticScore = hit.SemanticScore is { } s ? (decimal)s : null,
            });
        }

        request.DurationMs = (int)started.ElapsedMilliseconds;

        db.ScreeningRequests.Add(request);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return request.Id;
    }

    private async Task<Guid> CurrentAlgorithmIdAsync(CancellationToken ct) =>
        await db.MatchAlgorithms
            .Where(a => a.IsActive && a.Code == scoring.AlgorithmCode)
            .OrderByDescending(a => a.CreatedAt)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false) is var id && id != Guid.Empty
            ? id
            : throw new InvalidOperationException(
                $"No active row in match_algorithm for code '{scoring.AlgorithmCode}'. " +
                "Every screening names the algorithm that produced it, so a strategy without a row cannot run.");
}
