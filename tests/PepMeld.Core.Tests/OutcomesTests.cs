using PepMeld.Core.Checks;
using PepMeld.Core.Domain;
using Xunit;

namespace PepMeld.Core.Tests;

public class OutcomesTests
{
    private static readonly CoverageView Psp = new("CZ_PSP", Country.CZ, "PSP", true, new DateTimeOffset(2026, 10, 20, 6, 0, 0, TimeSpan.Zero), 1648);

    private static FindingView Function(EvidenceLevel level, DateOnly? from, DateOnly? to, int? monthsSinceEnd = null, string role = "poslanec") =>
        new(FindingKind.Function, level,
            new FindingPerson("Petr Fiala", new DateOnly(1964, 9, 1), level == EvidenceLevel.A, null),
            new FindingFunction(Country.CZ, PepCategory.MemberOfParliament, "§ 4", "čl. 2", "Poslanecká sněmovna", role, from, to, false, null),
            null, null, monthsSinceEnd, [], "x", null);

    [Fact]
    public void ACurrentConfirmedFunctionIsPep()
    {
        var (outcome, summary) = Outcomes.Derive(
            [Function(EvidenceLevel.A, new DateOnly(2021, 11, 28), new DateOnly(2025, 12, 15), 10, "Předseda vlády"),
             Function(EvidenceLevel.A, new DateOnly(2025, 10, 4), null)],
            [Psp], 12);

        Assert.Equal(CheckOutcome.Pep, outcome);
        Assert.Contains("poslanec", summary);
        Assert.Contains("trvá", summary);
    }

    [Fact]
    public void OnlyEndedConfirmedFunctionsAreFormerPep_WithinTwelveMonthsSaysSo()
    {
        var (outcome, summary) = Outcomes.Derive(
            [Function(EvidenceLevel.A, new DateOnly(2021, 11, 28), new DateOnly(2025, 12, 15), 10, "Předseda vlády")],
            [Psp], 12);

        Assert.Equal(CheckOutcome.FormerPep, outcome);
        Assert.Contains("zvýšená starostlivosť trvá", summary);
    }

    [Fact]
    public void OnlyEndedConfirmedFunctionsAreFormerPep_AfterTwelveMonthsSaysThatToo()
    {
        var (outcome, summary) = Outcomes.Derive(
            [Function(EvidenceLevel.A, new DateOnly(2013, 3, 8), new DateOnly(2023, 3, 8), 43, "Prezident")],
            [Psp], 12);

        Assert.Equal(CheckOutcome.FormerPep, outcome);
        Assert.Contains("uplynula", summary);
    }

    [Fact]
    public void ANameMatchWithoutADateOfBirthIsUnverified()
    {
        var (outcome, summary) = Outcomes.Derive([Function(EvidenceLevel.C, new DateOnly(2022, 10, 1), null)], [Psp], 12);

        Assert.Equal(CheckOutcome.Unverified, outcome);
        Assert.Contains("nemožno potvrdiť ani vylúčiť", summary);
    }

    [Fact]
    public void AConfirmedFunctionOutranksAnUnverifiedNamesake()
    {
        var (outcome, _) = Outcomes.Derive(
            [Function(EvidenceLevel.C, new DateOnly(2022, 10, 1), null), Function(EvidenceLevel.A, new DateOnly(2025, 10, 4), null)],
            [Psp], 12);

        Assert.Equal(CheckOutcome.Pep, outcome);
    }

    [Fact]
    public void NothingFoundNamesTheListsSearched()
    {
        var (outcome, summary) = Outcomes.Derive([], [Psp], 12);

        Assert.Equal(CheckOutcome.NoMatch, outcome);
        Assert.Contains("CZ_PSP", summary);
    }

    [Fact]
    public void NothingFoundInNoListsIsSaidPlainly()
    {
        var (outcome, summary) = Outcomes.Derive([], [Psp with { LastImportedAt = null, Persons = 0 }], 12);

        Assert.Equal(CheckOutcome.NoMatch, outcome);
        Assert.Contains("nebol importovaný", summary);
    }

    [Theory]
    [InlineData(2026, 1, 15, 2026, 10, 20, 9)]
    [InlineData(2025, 10, 25, 2026, 10, 20, 11)]
    [InlineData(2025, 10, 20, 2026, 10, 20, 12)]
    [InlineData(2024, 10, 20, 2026, 10, 20, 24)]
    [InlineData(2026, 10, 1, 2026, 10, 20, 0)]
    public void MonthsBetween_CountsWholeMonths(int fy, int fm, int fd, int ty, int tm, int td, int expected)
        => Assert.Equal(expected, ListsStage.MonthsBetween(new DateOnly(fy, fm, fd), new DateOnly(ty, tm, td)));
}
