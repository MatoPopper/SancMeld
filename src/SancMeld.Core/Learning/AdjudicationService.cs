using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;

namespace SancMeld.Core.Learning;

/// <summary>
/// Records what an analyst decided, and turns it into training data.
///
/// This closes the loop the whole system is arranged around. Until an analyst says which
/// candidate was the person, the matcher has no way to be wrong in a way anyone can
/// measure, and no model can be trained on anything but guesses about spellings.
///
/// One review labels the whole candidate set, not one pair — a check averages 18
/// candidates, so a few hundred reviews is a corpus of several thousand comparisons.
/// </summary>
public sealed class AdjudicationService(SancMeldDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<AdjudicationReasonView>> ReasonsAsync(CancellationToken ct = default) =>
        await db.AdjudicationReasons
            .OrderBy(r => r.IsNegative)
            .ThenBy(r => r.Code)
            .Select(r => new AdjudicationReasonView(r.Code, r.Label, r.IsNegative))
            .ToListAsync(ct)
            .ConfigureAwait(false);

    public async Task<AdjudicationResult> RecordAsync(AdjudicationRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        var candidates = await db.ScreeningCandidates
            .Where(c => c.RequestId == request.CheckId)
            .ToDictionaryAsync(c => c.DesignationId, ct)
            .ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            throw new InvalidOperationException(
                $"No screening request {request.CheckId}. An adjudication has to attach to the check it "
                + "was made against, or the decision cannot be reconstructed later.");
        }

        var algorithm = await db.MatchAlgorithms
            .Where(a => a.IsActive)
            .OrderByDescending(a => a.CreatedAt)
            .FirstAsync(ct)
            .ConfigureAwait(false);

        var validReasons = await db.AdjudicationReasons
            .Select(r => r.Code)
            .ToHashSetAsync(ct)
            .ConfigureAwait(false);

        var now = clock.GetUtcNow();
        var warnings = new List<string>();
        var adjudications = new List<Adjudication>();
        var samples = new List<TrainingSample>();

        foreach (var decision in request.Decisions)
        {
            if (!candidates.TryGetValue(decision.DesignationId, out var candidate))
            {
                // Deciding on something the check never returned means the two are out of
                // step. Recording it anyway would put a decision in the trail that the
                // evidence does not support.
                warnings.Add($"Listing {decision.DesignationId} was not among the candidates of this check; ignored.");
                continue;
            }

            if (decision.ReasonCode is { Length: > 0 } reason && !validReasons.Contains(reason))
            {
                warnings.Add($"Unknown reason code '{reason}' on listing {decision.DesignationId}; recorded without it.");
            }

            adjudications.Add(new Adjudication
            {
                CandidateId = candidate.Id,
                Decision = decision.Decision,
                ReasonCode = decision.ReasonCode is { Length: > 0 } r && validReasons.Contains(r) ? r : null,
                Note = decision.Note,
                DecidedBy = request.DecidedBy,
                DecidedAt = now,
                DurationMs = request.DurationMs,
                SupersedesId = decision.Supersedes,
            });

            if (Label(decision.Decision) is { } label)
            {
                samples.Add(await BuildSampleAsync(candidate, decision, label, algorithm, now, ct).ConfigureAwait(false));
            }
        }

        db.Adjudications.AddRange(adjudications);
        db.TrainingSamples.AddRange(samples);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AdjudicationResult
        {
            CheckId = request.CheckId,
            Recorded = adjudications.Count,
            Labelled = samples.Count,
            Warnings = warnings,
        };
    }

    /// <summary>
    /// Match and NoMatch are labels. Undecided and NeedsMoreData are not, and collapsing
    /// them into "no" is how a corpus quietly fills with false negatives — the analyst did
    /// not say no, they said they could not tell.
    /// </summary>
    private static bool? Label(AdjudicationDecision decision) => decision switch
    {
        AdjudicationDecision.Match => true,
        AdjudicationDecision.NoMatch => false,
        _ => null,
    };

    /// <summary>
    /// Carries the feature vector the decision was made against, and nothing that could
    /// identify a person. The corpus has to survive being copied to wherever a model is
    /// trained, and a training set full of customer names would not.
    /// </summary>
    private async Task<TrainingSample> BuildSampleAsync(
        ScreeningCandidate candidate,
        AdjudicationDecisionInput decision,
        bool label,
        MatchAlgorithm algorithm,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var designation = await db.Designations
            .Where(d => d.Id == candidate.DesignationId)
            .Select(d => new { d.SubjectType, d.Source!.Code, d.Source.LegalEffect })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new TrainingSample
        {
            Features = candidate.Features,
            Label = label,

            SourceCode = designation?.Code,
            LegalEffect = designation?.LegalEffect,
            SubjectType = designation?.SubjectType,

            // Candidates below the threshold are the half of the corpus a model would
            // never see otherwise, and the only guard against it learning its own output.
            WasExploration = candidate.IsExploration,
            ReasonCode = decision.ReasonCode,

            FeatureSetVersion = algorithm.FeatureSetVersion,
            NameNormVersion = algorithm.NameNormVersion,

            // Month rather than timestamp: enough to slice the corpus by period and to
            // apply a retention rule, not enough to place a person at a moment.
            PeriodMonth = new DateOnly(now.Year, now.Month, 1),
            Origin = "ADJUDICATION",
        };
    }

    /// <summary>
    /// The corpus, for training a model somewhere else. Paged by id so an export can be
    /// resumed and repeated without missing or repeating a row.
    /// </summary>
    public async Task<IReadOnlyList<TrainingSampleView>> SamplesAsync(
        long afterId = 0,
        int limit = 1000,
        string? origin = null,
        CancellationToken ct = default)
    {
        var query = db.TrainingSamples.AsNoTracking().Where(s => s.Id > afterId);

        if (origin is { Length: > 0 })
        {
            query = query.Where(s => s.Origin == origin);
        }

        return await query
            .OrderBy(s => s.Id)
            .Take(Math.Clamp(limit, 1, 10_000))
            .Select(s => new TrainingSampleView(
                s.Id, s.Features, s.Label, s.Origin, s.SourceCode, s.LegalEffect, s.SubjectType,
                s.WasExploration, s.ReasonCode, s.FeatureSetVersion, s.NameNormVersion, s.PeriodMonth))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }
}
