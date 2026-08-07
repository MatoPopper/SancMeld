using SancMeld.Core.Abstractions;

namespace SancMeld.Core.Search;

/// <summary>
/// Hand-weighted scoring — what runs until there are adjudications to learn from.
///
/// The weights are asymmetric on purpose. Agreement nudges a candidate up; contradiction
/// pushes it down harder, because two people sharing a name but not a birth year is the
/// common false positive. Neither is decisive on its own: a contradicted birth year lowers
/// a hit, it does not remove it, because publishers disagree about dates for the same
/// subject often enough to matter — 21 of 1 244 EU/FR cross-referenced pairs disagree on
/// the birth year alone.
///
/// These numbers are a starting point, not a result. They are the thing a model should
/// eventually replace, which is why every screening stores the feature vector they were
/// applied to.
/// </summary>
public sealed class RulesScoringStrategy : IScoringStrategy
{
    public const double BirthYearAgrees = 0.15;
    public const double BirthYearContradicts = -0.35;
    public const double CountryAgrees = 0.05;
    public const double CountryContradicts = -0.10;

    public string AlgorithmCode => "RULES";

    public double Score(MatchExplanation explanation)
    {
        ArgumentNullException.ThrowIfNull(explanation);

        // An exact identifier is not a similarity question. Nothing else raises or lowers it.
        if (explanation.IdentifierMatch)
        {
            return 1.0;
        }

        // A person is not a company. The candidate is still returned and still recorded —
        // dropping it would keep it out of the training data too — but it is not put in
        // front of an analyst.
        if (explanation.SubjectType == QualifierState.Contradicts)
        {
            return 0;
        }

        var score = explanation.NameScore
            + explanation.BirthYear switch
            {
                QualifierState.Agrees => BirthYearAgrees,
                QualifierState.Contradicts => BirthYearContradicts,

                // NotProvided and UnknownOnRecord both mean "no evidence", and both must
                // score zero. This single line is the difference between a system that
                // works on sanctions data and one that buries every sparse listing.
                _ => 0,
            }
            + explanation.Country switch
            {
                QualifierState.Agrees => CountryAgrees,
                QualifierState.Contradicts => CountryContradicts,
                _ => 0,
            };

        return Math.Clamp(score, 0, 1);
    }
}
