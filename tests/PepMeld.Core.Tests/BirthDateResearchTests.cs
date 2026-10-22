using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.Tests;

public sealed class BirthDateResearchTests
{
    [Theory]
    [InlineData("Narodil se 12. června 1966 v Kladně.")]
    [InlineData("narozen 12. 6. 1966")]
    [InlineData("Narodil sa 12. júna 1966.")]
    [InlineData("born June 12, 1966")]
    [InlineData("(* 12.06.1966, Kladno)")]
    public void AQuoteThatStatesTheDatePasses(string quote) =>
        Assert.True(BirthDateResearch.StatesDate(quote, new DateOnly(1966, 6, 12)));

    [Theory]
    [InlineData("Narodil se 12. července 1966.")] // July, not June
    [InlineData("Narodil se v roce 1966.")]
    [InlineData("Senátor Jan Novák (1966)")]
    [InlineData("narozen 13. 6. 1966")]
    public void AQuoteThatDoesNotStateTheDateFails(string quote) =>
        Assert.False(BirthDateResearch.StatesDate(quote, new DateOnly(1966, 6, 12)));

    [Fact]
    public void ADayEqualToTheMonthMustAppearTwice()
    {
        Assert.True(BirthDateResearch.StatesDate("narozen 3. 3. 1970", new DateOnly(1970, 3, 3)));
        Assert.True(BirthDateResearch.StatesDate("narozen 3. března 1970", new DateOnly(1970, 3, 3)));
        Assert.False(BirthDateResearch.StatesDate("narozen 3. 1970", new DateOnly(1970, 3, 3)));
    }
}

public sealed class BirthDateFillRulesTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    private static Data.PepFunction F(string role, DateOnly? from, DateOnly? to, PepMeld.Core.Domain.PepCategory category = PepMeld.Core.Domain.PepCategory.RegionalLeader) =>
        new() { SourceKey = "k", LegalBasis = "x", Institution = "i", Role = role, Country = PepMeld.Core.Domain.Country.CZ, Category = category, ValidFrom = from, ValidTo = to };

    [Theory]
    [InlineData("primátor", "primátor Brna", true)]
    [InlineData("hejtman", "hejtman Plzeňského kraje", true)]
    [InlineData("ministr", "ministr financí České republiky", true)]
    [InlineData("senátor", "senátor", true)]
    [InlineData("senátor", "poslanec", false)]
    [InlineData("starosta obce s rozšířenou působností", "primátor Brna", false)]
    public void ARoleIsTheSameTitle(string a, string b, bool same)
    {
        Assert.Equal(same, BirthDateFill.SameRole(a, b));
        Assert.Equal(same, BirthDateFill.SameRole(b, a));
    }

    [Fact]
    public void AnOfficeWithNoDatesIsHeldNowAndMeetsOnlyACurrentTerm()
    {
        var register = F("hejtman", null, null);

        Assert.True(BirthDateFill.SameOffice([register], [F("hejtman Jihomoravského kraje", new DateOnly(2024, 10, 30), null)], Today));
        Assert.False(BirthDateFill.SameOffice([register], [F("hejtman Jihomoravského kraje", new DateOnly(2004, 11, 20), new DateOnly(2008, 11, 19))], Today));
        Assert.False(BirthDateFill.SameOffice([register], [F("hejtman", null, null, PepMeld.Core.Domain.PepCategory.GovernmentMember)], Today));
    }
}

public sealed class ElectionAgesRulesTests
{
    [Fact]
    public void ACsvLineKeepsACommaInsideQuotes() =>
        Assert.Equal(["1", "Ing.", "starosta, podnikatel", "a \"b\""], ElectionAges.Csv.Split("1,Ing.,\"starosta, podnikatel\",\"a \"\"b\"\"\""));

    [Theory]
    [InlineData("Město Chrudim", "starosta obce s rozšířenou působností", "chrudim")]
    [InlineData("Městská část Praha 17", "starosta městské části", "praha 17")]
    [InlineData("Statutární Město Liberec", "primátor", "liberec")]
    [InlineData("Jihomoravský kraj", "hejtman", null)]
    public void TheCouncilIsTheTownOfTheOffice(string institution, string role, string? town)
    {
        var f = new Data.PepFunction { SourceKey = "k", LegalBasis = "x", Institution = institution, Role = role, Category = PepMeld.Core.Domain.PepCategory.RegionalLeader };
        Assert.Equal(town, ElectionAges.Town([f]));
    }
}
