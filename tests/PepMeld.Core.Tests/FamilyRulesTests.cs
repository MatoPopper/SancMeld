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

    private static readonly DateOnly Today = new(2026, 10, 21);
    private static readonly DateOnly Born = new(1960, 1, 1);

    private static FamilyLink TieOf(string pep, string evidenceUrl = "https://example.org/a") => new()
    {
        PepName = pep,
        PepNameNormalized = pep,
        PepBirthDate = Born,
        Relation = FamilyRelation.Spouse,
        RelativeName = "y",
        RelativeNameNormalized = "y",
        Status = "current",
        Confidence = "high",
        EvidenceJson = $$"""[ { "url": "{{evidenceUrl}}" } ]""",
    };

    private static readonly DateOnly? Lasts = null;

    private static FamilyStage.PepHolder Holder(string name, params DateOnly?[] ends) => new(name, Born, "https://www.wikidata.org/wiki/Q7", ends);

    [Fact]
    public void ATieOfACurrentPepCounts()
        => Assert.True(FamilyStage.Standing(TieOf("jan novak"), [Holder("novak jan", new DateOnly(2010, 1, 1), null)], Today, 12).Counts);

    [Fact]
    public void ATieOfAPepWhoLeftWithinAYearCountsAndSaysWhen()
    {
        var (counts, text) = FamilyStage.Standing(TieOf("jan novak"), [Holder("jan novak", new DateOnly(2026, 3, 1))], Today, 12);

        Assert.True(counts);
        Assert.Contains("1. 3. 2026", text, StringComparison.Ordinal);
    }

    /// <summary>A deputy of 1996 is no PEP today, and their relatives no family members of one.</summary>
    [Fact]
    public void ATieOfAPepWhoLeftLongAgoIsInformationOnly()
    {
        var (counts, text) = FamilyStage.Standing(TieOf("jan novak"), [Holder("jan novak", new DateOnly(1998, 6, 30))], Today, 12);

        Assert.False(counts);
        Assert.Contains("uplynula", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ATieOfAPepTheListsDoNotNameIsInformationOnly()
    {
        Assert.False(FamilyStage.Standing(TieOf("jan novak"), [Holder("petr svoboda", Lasts)], Today, 12).Counts);
        Assert.False(FamilyStage.Standing(TieOf("jan novak"), [Holder("jan novak", Lasts) with { BirthDate = Born.AddDays(1) }], Today, 12).Counts);
    }

    /// <summary>Wikidata's label and the register's name may differ; the Wikidata page the tie cites finds the PEP anyway.</summary>
    [Fact]
    public void ThePepIsFoundByTheWikidataPageTheTieCites()
    {
        var tie = TieOf("john novak", "https://www.wikidata.org/wiki/Q7");

        Assert.True(FamilyStage.Standing(tie, [Holder("jan novak", Lasts)], Today, 12).Counts);
        Assert.True(FamilyStage.Standing(TieOf("jan novak"), [Holder("jan maria novak", Lasts)], Today, 12).Counts);
    }

    /// <summary>One circle for both countries, the wider Czech one: every tie counts, a sibling of a Slovak PEP too.</summary>
    [Theory]
    [InlineData(Country.SK, FamilyRelation.Spouse)]
    [InlineData(Country.SK, FamilyRelation.Sibling)]
    [InlineData(Country.SK, FamilyRelation.Grandchild)]
    [InlineData(Country.SK, FamilyRelation.ParentInLaw)]
    [InlineData(Country.CZ, FamilyRelation.SiblingInLaw)]
    [InlineData(Country.CZ, FamilyRelation.Grandparent)]
    public void EveryTieCountsInBothCountries(Country country, FamilyRelation relation)
        => Assert.True(FamilyStage.Relevance(Link(country, relation)).Relevant);

    [Fact]
    public void TheTextSaysWhereTheTieGoesBeyondTheSlovakLaw()
    {
        Assert.Contains("§ 6 ods. 3", FamilyStage.Relevance(Link(Country.SK, FamilyRelation.Child)).Law, StringComparison.Ordinal);
        Assert.Contains("nemenuje", FamilyStage.Relevance(Link(Country.SK, FamilyRelation.Sibling)).Law, StringComparison.Ordinal);
        Assert.Contains("nemenuje", FamilyStage.Relevance(Link(Country.CZ, FamilyRelation.ParentInLaw)).Law, StringComparison.Ordinal);
    }

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

    /// <summary>"ml." from the agent and "mladší" from Wikidata are the same son, not two.</summary>
    [Theory]
    [InlineData("andrej babis ml", "andrej babis mladsi")]
    [InlineData("jan novak st", "jan novak")]
    public void TheJuniorWordDoesNotMakeASecondRelative(string agent, string wikidata)
    {
        var link = new FamilyLink
        {
            PepName = "x",
            PepNameNormalized = "x",
            RelativeName = wikidata,
            RelativeNameNormalized = wikidata,
            Status = "current",
            Confidence = "high",
            EvidenceJson = "[]",
        };

        Assert.Same(link, FamilyLinkMerge.FindExisting([link], agent));
    }

    [Fact]
    public void TwoDifferentChildrenOfTheSameSurnameStayTwo()
    {
        var link = new FamilyLink
        {
            PepName = "x",
            PepNameNormalized = "x",
            RelativeName = "Eva Novakova",
            RelativeNameNormalized = "eva novakova",
            Status = "current",
            Confidence = "high",
            EvidenceJson = "[]",
        };

        Assert.Null(FamilyLinkMerge.FindExisting([link], "jana novakova"));
    }
}
