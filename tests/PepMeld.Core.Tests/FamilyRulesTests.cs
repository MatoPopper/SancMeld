using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using Xunit;

namespace PepMeld.Core.Tests;

public class FamilyRulesTests
{
    private static FamilyLink Link(Country country, FamilyRelation relation) => new()
    {
        PepName = "x",
        PepNameNormalized = "x",
        PepCountry = country,
        Relation = relation,
        RelativeName = "y",
        RelativeNameNormalized = "y",
        Status = "current",
        Confidence = "high",
        EvidenceJson = "[]",
    };

    [Theory]
    [InlineData(Country.SK, FamilyRelation.Spouse, true)]
    [InlineData(Country.SK, FamilyRelation.ChildInLaw, true)]
    [InlineData(Country.SK, FamilyRelation.Sibling, false)]
    [InlineData(Country.CZ, FamilyRelation.Sibling, true)]
    public void TheLawOfThePepsCountryDecidesWhetherATieCounts(Country country, FamilyRelation relation, bool relevant)
        => Assert.Equal(relevant, FamilyStage.Relevance(Link(country, relation)).Relevant);

    [Fact]
    public void ARecordedBirthdayDecides()
    {
        Assert.True(FamilyStage.IsThisPerson("michal fico", new DateOnly(1994, 1, 1), "michal fico", new DateOnly(1994, 1, 1), null, 0.6, out var matched));
        Assert.True(matched);
        Assert.False(FamilyStage.IsThisPerson("michal fico", new DateOnly(1994, 1, 2), "michal fico", new DateOnly(1994, 1, 1), null, 0.6, out _));

        // With the date agreeing, a surname is enough, as for a PEP.
        Assert.True(FamilyStage.IsThisPerson("fico", new DateOnly(1994, 1, 1), "michal fico", new DateOnly(1994, 1, 1), null, 0.6, out _));
    }

    [Fact]
    public void WithoutABirthdayTheWholeNameIsNeededAndAYearMustAgree()
    {
        Assert.True(FamilyStage.IsThisPerson("pavlina matovicova", new DateOnly(1975, 1, 1), "pavlina matovicova", null, null, 0.6, out var matched));
        Assert.False(matched);
        Assert.False(FamilyStage.IsThisPerson("matovicova", new DateOnly(1975, 1, 1), "pavlina matovicova", null, null, 0.6, out _));
        Assert.False(FamilyStage.IsThisPerson("pavlina matovicova", new DateOnly(1975, 1, 1), "pavlina matovicova", null, 1976, 0.6, out _));

        // A sister is not her sister's namesake because the trigrams are close.
        Assert.False(FamilyStage.IsThisPerson("eva pavlova", new DateOnly(1975, 1, 1), "ema pavlova", null, null, 0.6, out _));
        Assert.True(FamilyStage.IsThisPerson("svetlana ficova", new DateOnly(1965, 1, 1), "svetlana ficova rod svobodova", null, null, 0.6, out _));
    }

    [Theory]
    [InlineData("Jeho manželkou je Pavlína.", "<p>Jeho  manželkou je <a href=x>Pavlína</a>.[3]</p>", QuoteCheck.Exact)]
    [InlineData("Manželka: Svetlana Ficová, rod. Svobodová", "<td>Manželka</td><td>Svetlana Ficová, rod. Svobodová</td>", QuoteCheck.Partial)]
    [InlineData("syn Michal pracuje pre ČEZ", "<p>nič také</p>", QuoteCheck.NotFound)]
    public void AQuoteIsLookedForInTheFoldedPageText(string quote, string html, QuoteCheck expected)
    {
        var text = HttpQuoteVerifier.Fold(System.Text.RegularExpressions.Regex.Replace(html, "<[^>]+>", " "));
        Assert.Equal(expected, HttpQuoteVerifier.Find(HttpQuoteVerifier.Fold(quote), text));
    }
}
