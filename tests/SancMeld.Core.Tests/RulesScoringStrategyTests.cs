using SancMeld.Core.Search;
using Xunit;

namespace SancMeld.Core.Tests;

/// <summary>
/// The one property that has to hold whatever the weights become, and whatever replaces
/// them: a listing that does not record a qualifier must score exactly the same as one
/// where the caller never asked about it.
///
/// On the EU list alone, 38 % of subjects carry no date of birth and 68 % carry no
/// identifier. If "unknown" ever starts behaving like "disagrees", those are precisely the
/// subjects that drop below the threshold — the ones the publishers know least about, and
/// a missed match is a legal breach.
/// </summary>
public class RulesScoringStrategyTests
{
    private readonly RulesScoringStrategy _scoring = new();

    private static MatchExplanation Explanation(
        double nameScore = 0.60,
        bool identifier = false,
        QualifierState birthYear = QualifierState.NotProvided,
        QualifierState country = QualifierState.NotProvided,
        QualifierState subjectType = QualifierState.NotProvided) => new()
        {
            NameScore = nameScore,
            IdentifierMatch = identifier,
            BirthYear = birthYear,
            Country = country,
            SubjectType = subjectType,
        };

    [Theory]
    [InlineData(QualifierState.NotProvided)]
    [InlineData(QualifierState.UnknownOnRecord)]
    public void AbsentEvidenceNeitherHelpsNorHurts(QualifierState state)
    {
        var baseline = _scoring.Score(Explanation());

        Assert.Equal(baseline, _scoring.Score(Explanation(birthYear: state)));
        Assert.Equal(baseline, _scoring.Score(Explanation(country: state)));
        Assert.Equal(baseline, _scoring.Score(Explanation(subjectType: state)));
    }

    [Fact]
    public void AgreementRaisesAndContradictionLowers()
    {
        var baseline = _scoring.Score(Explanation());

        Assert.True(_scoring.Score(Explanation(birthYear: QualifierState.Agrees)) > baseline);
        Assert.True(_scoring.Score(Explanation(birthYear: QualifierState.Contradicts)) < baseline);
    }

    [Fact]
    public void ContradictedBirthYearLowersAHitWithoutRemovingIt()
    {
        // Publishers disagree about dates for the same subject often enough to matter:
        // 21 of 1 244 EU/FR cross-referenced pairs disagree on the birth year alone.
        var score = _scoring.Score(Explanation(nameScore: 0.95, birthYear: QualifierState.Contradicts));

        Assert.True(score > 0, "A single contradicted qualifier must not delete a strong name match.");
        Assert.True(score < 0.95);
    }

    [Fact]
    public void ExactIdentifierSettlesItRegardlessOfTheName()
    {
        // The deterministic layer exists for subjects whose name was transliterated beyond
        // any trigram's reach: nothing was matched on the name at all here.
        var score = _scoring.Score(Explanation(nameScore: 0, identifier: true, birthYear: QualifierState.Contradicts));

        Assert.Equal(1.0, score);
    }

    [Fact]
    public void PersonQueryDoesNotMatchAnOrganisation()
    {
        var score = _scoring.Score(Explanation(nameScore: 0.99, subjectType: QualifierState.Contradicts));

        Assert.Equal(0, score);
    }

    [Theory]
    [InlineData(0.99, QualifierState.Agrees)]
    [InlineData(0.01, QualifierState.Contradicts)]
    public void ScoreStaysWithinRange(double nameScore, QualifierState birthYear)
    {
        var score = _scoring.Score(Explanation(nameScore, birthYear: birthYear, country: birthYear));

        Assert.InRange(score, 0, 1);
    }
}
