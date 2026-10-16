using SancMeld.Core.Relations;
using Xunit;

namespace SancMeld.Connectors.Wikidata.Tests;

public sealed class WikidataPersonMatcherTests
{
    private static readonly Guid Source = Guid.NewGuid();

    private static PendingPerson Person(params string[] names) =>
        new(Guid.NewGuid(), Source, "rec-1", [new DateOnly(1976, 10, 5)], names);

    [Fact]
    public void TheSameSpellingResolves()
    {
        var outcome = WikidataPersonMatcher.Match(
            Person("Ramzan Akhmadovitch KADYROV"),
            [new RegisterCandidate("Q217115", "Ramzan Kadyrov", ["Ramzan Akhmadovich Kadyrov", "Рамзан Кадыров"])],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.NotNull(outcome.Match);
        var match = outcome.Match;
        Assert.Equal("Q217115", match.ExternalId);
        Assert.Equal("Ramzan Kadyrov", match.Label);
        Assert.True(match.NameScore >= 0.6, $"score was {match.NameScore}");
    }

    [Fact]
    public void ACyrillicAliasMeetsTheRussianLabel()
    {
        // The listed Latin spelling and the register's English label differ enough to
        // fail on their own; the Cyrillic alias on the list is what carries it.
        var outcome = WikidataPersonMatcher.Match(
            Person("KADYROV Ramzan", "Рамзан Ахматович КАДЫРОВ"),
            [new RegisterCandidate("Q217115", "Ramzan Kadyrov", ["Ramzan Akhmadovich Kadyrov", "Рамзан Ахматович Кадыров"])],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.NotNull(outcome.Match);
        var match = outcome.Match;
        Assert.Equal("Рамзан Ахматович КАДЫРОВ", match.MatchedName);
        Assert.Equal("Рамзан Ахматович Кадыров", match.MatchedLabel);
    }

    [Fact]
    public void APatronymicMissingOnOneSideStillResolves()
    {
        // Trigram similarity of this pair is below the floor; containment of both words
        // is what the register-versus-list difference most often looks like.
        var outcome = WikidataPersonMatcher.Match(
            Person("Ramzan Kadyrov"),
            [new RegisterCandidate("Q217115", "Ramzan Kadyrov", ["Ramzan Akhmadovich Kadyrov"])],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.NotNull(outcome.Match);
        Assert.Equal(WikidataPersonMatcher.ContainmentScore, outcome.Match.NameScore);
    }

    [Fact]
    public void ASurnameAloneIsNotEnough()
    {
        var outcome = WikidataPersonMatcher.Match(
            Person("Kadyrov"),
            [new RegisterCandidate("Q1", "Someone Kadyrov", ["Someone Kadyrov"])],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.Null(outcome.Match);
        Assert.Contains("below", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void ASharedGivenNameAndPatronymicDoNotMakeTwoMenOne()
    {
        // Found on the first live run (2026-10-16): the whole-name similarity of this
        // pair is 0.62, above the floor, and the surnames have nothing in common.
        var outcome = WikidataPersonMatcher.Match(
            Person("Александр Владимирович КОРОТКИЙ"),
            [new RegisterCandidate("Q6462862", "Alexander Schott", ["Александр Владимирович Шотт"])],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.Null(outcome.Match);
    }

    [Fact]
    public void ATransliterationsDifferenceInOneWordIsStillTheSameWord()
    {
        Assert.True(WikidataPersonMatcher.WordsCovered("ramzan akhmadovitch kadyrov", "ramzan akhmadovich kadyrov"));
        Assert.True(WikidataPersonMatcher.WordsCovered("yong mu ri", "ri yong mu"));
        Assert.False(WikidataPersonMatcher.WordsCovered("aleksandr vladimirovich korotkiy", "aleksandr vladimirovich shott"));
    }

    [Fact]
    public void ANamesakeBornTheSameDayIsNotTheListedPerson()
    {
        var outcome = WikidataPersonMatcher.Match(
            Person("Ivan Petrovich Sidorov"),
            [new RegisterCandidate("Q2", "Ivan Ivanov", ["Ivan Ivanov"]), new RegisterCandidate("Q3", "Maria Sidorova", ["Maria Sidorova"])],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.Null(outcome.Match);
    }

    [Fact]
    public void TwoRecordsTooCloseToTellApartResolveToNeither()
    {
        var outcome = WikidataPersonMatcher.Match(
            Person("Ivan Petrov"),
            [new RegisterCandidate("Q4", "Ivan Petrov", ["Ivan Petrov"]), new RegisterCandidate("Q5", "Ivan Petrov", ["Ivan Petrov"])],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.Null(outcome.Match);
        Assert.Contains("Ambiguous", outcome.Note, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBestOfSeveralCandidatesWins()
    {
        var outcome = WikidataPersonMatcher.Match(
            Person("Ramzan Kadyrov"),
            [
                new RegisterCandidate("Q6", "Ramzan Kadyrovich", ["Ramzan Kadyrovich"]),
                new RegisterCandidate("Q217115", "Ramzan Kadyrov", ["Ramzan Kadyrov"]),
            ],
            minNameScore: 0.6, ambiguityMargin: 0.05);

        Assert.Equal("Q217115", outcome.Match?.ExternalId);
    }

    [Fact]
    public void NothingToCompareIsNoMatchAndNoNote()
    {
        var outcome = WikidataPersonMatcher.Match(Person("Ramzan Kadyrov"), [], 0.6, 0.05);

        Assert.Null(outcome.Match);
        Assert.Null(outcome.Note);
    }
}
