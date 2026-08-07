using SancMeld.Core.Search;

namespace SancMeld.Core.Abstractions;

/// <summary>
/// Turns the evidence about one candidate into a single score.
///
/// This is the seam a trained model plugs into. Candidate generation stays as it is —
/// measured against the full EU list, plain trigram ranking beat a trained LightGBM ranker
/// on names by a wide margin (recall@1 68 % vs 36 %, recall@10 100 % vs 87 %), so replacing
/// retrieval with a model would lose matches outright. What a model can plausibly improve
/// is exactly this step: weighing qualifiers that are missing far more often than they are
/// present.
///
/// A new strategy is a new implementation AND a new row in match_algorithm, never an edit
/// to an existing one. Every screening decision names the algorithm that produced it, and
/// a past decision must keep pointing at something that still means what it meant.
/// </summary>
public interface IScoringStrategy
{
    /// <summary>Matches match_algorithm.code — 'RULES' today, 'ML_LIGHTGBM' later.</summary>
    string AlgorithmCode { get; }

    /// <summary>
    /// Implementations must treat <see cref="QualifierState.UnknownOnRecord"/> as evidence
    /// of nothing. Most listings are incomplete; scoring absence as disagreement pushes
    /// precisely the subjects nobody knows much about below the threshold.
    /// </summary>
    double Score(MatchExplanation explanation);
}
