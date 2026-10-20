using PepMeld.Core.Checks;
using PepMeld.Core.Domain;
using Xunit;

namespace PepMeld.Core.Tests;

public class FindingMergerTests
{
    private static readonly DateOnly Born = new(1954, 9, 2);

    private static FindingView Finding(
        string source,
        EvidenceLevel level,
        DateOnly? from,
        DateOnly? to,
        PepCategory category = PepCategory.GovernmentMember,
        DateOnly? dob = null,
        string name = "Andrej Babiš",
        Country country = Country.CZ) =>
        new(FindingKind.Function, level,
            new FindingPerson(name, dob ?? Born, dob is not null || level != EvidenceLevel.C, null),
            new FindingFunction(country, category, "§", "čl.", source + " institution", "Předseda vlády", from, to, false, null),
            null, null, null,
            [new Evidence("https://" + source, null, null)],
            $"{source} says so.",
            null,
            [source]);

    [Fact]
    public void ALesserRowAboutTheSameOfficeIsFoldedIntoTheBetterOne()
    {
        var psp = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2017, 12, 6), new DateOnly(2021, 12, 17));
        var wikidata = Finding("CZ_WIKIDATA", EvidenceLevel.B, new DateOnly(2017, 12, 13), new DateOnly(2021, 12, 17));

        var merged = FindingMerger.Merge([psp, wikidata]);

        var only = Assert.Single(merged);
        Assert.Equal(EvidenceLevel.A, only.Level);
        Assert.Equal(2, only.Evidence.Count);
        Assert.Equal(["CZ_PSP", "CZ_WIKIDATA"], only.Sources);
        Assert.Contains("CZ_WIKIDATA", only.Explanation);
        Assert.Contains("úroveň B", only.Explanation);
    }

    [Fact]
    public void TwoTermsFromOneSourceStayTwoRows()
    {
        var first = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2017, 12, 6), new DateOnly(2018, 6, 27));
        var second = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2018, 6, 27), new DateOnly(2021, 12, 17));

        Assert.Equal(2, FindingMerger.Merge([first, second]).Count);
    }

    [Fact]
    public void OneSpanOnWikidataIsFoldedIntoTheTermItOverlapsBest()
    {
        var first = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2017, 12, 6), new DateOnly(2018, 6, 27));
        var second = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2018, 6, 27), new DateOnly(2021, 12, 17));
        var wikidata = Finding("CZ_WIKIDATA", EvidenceLevel.B, new DateOnly(2017, 12, 13), new DateOnly(2021, 12, 17));

        var merged = FindingMerger.Merge([first, second, wikidata]);

        Assert.Equal(2, merged.Count);
        Assert.Single(merged, f => f.Sources!.Contains("CZ_WIKIDATA"));
    }

    [Fact]
    public void ARowWithoutADateOfBirthIsNeverFolded()
    {
        var confirmed = Finding("CZ_WIKIDATA", EvidenceLevel.B, new DateOnly(2022, 10, 1), null, PepCategory.MemberOfParliament);
        var namesake = Finding("CZ_PSP", EvidenceLevel.C, new DateOnly(2022, 10, 1), null, PepCategory.MemberOfParliament) with
        {
            Person = new FindingPerson("Andrej Babiš", null, false, null),
        };

        Assert.Equal(2, FindingMerger.Merge([confirmed, namesake]).Count);
    }

    [Fact]
    public void AnotherBirthdayOrAnotherCategoryIsAnotherFinding()
    {
        var keeper = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2017, 12, 6), null);
        var otherPerson = Finding("CZ_WIKIDATA", EvidenceLevel.B, new DateOnly(2017, 12, 6), null, dob: new DateOnly(1968, 11, 24));
        var otherCategory = Finding("CZ_WIKIDATA", EvidenceLevel.B, new DateOnly(2017, 12, 6), null, PepCategory.MemberOfParliament);

        Assert.Equal(3, FindingMerger.Merge([keeper, otherPerson, otherCategory]).Count);
    }

    [Fact]
    public void TermsThatDoNotOverlapAreNotTheSameOffice()
    {
        var keeper = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2014, 1, 29), new DateOnly(2017, 5, 24));
        var later = Finding("CZ_WIKIDATA", EvidenceLevel.B, new DateOnly(2021, 12, 17), null);

        Assert.Equal(2, FindingMerger.Merge([keeper, later]).Count);
    }

    [Fact]
    public void AnUndatedRowFoldsIntoTheOpenTermRatherThanAnEndedOne()
    {
        var ended = Finding("SK_NRSR", EvidenceLevel.A, new DateOnly(2020, 3, 20), new DateOnly(2023, 10, 25), PepCategory.MemberOfParliament, country: Country.SK);
        var open = Finding("SK_NRSR", EvidenceLevel.A, new DateOnly(2023, 10, 25), null, PepCategory.MemberOfParliament, country: Country.SK);
        var undated = Finding("SK_WIKIDATA", EvidenceLevel.B, null, null, PepCategory.MemberOfParliament, country: Country.SK);

        var merged = FindingMerger.Merge([ended, open, undated]);

        Assert.Equal(2, merged.Count);
        Assert.Contains("SK_WIKIDATA", merged.Single(f => f.Function!.To is null).Sources!);
        Assert.DoesNotContain("SK_WIKIDATA", merged.Single(f => f.Function!.To is not null).Sources!);
    }

    [Fact]
    public void TheOrderOfTheStageIsKept()
    {
        var a = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2025, 12, 9), null);
        var b = Finding("CZ_PSP", EvidenceLevel.A, new DateOnly(2014, 1, 29), new DateOnly(2017, 5, 24));
        var c = Finding("CZ_WIKIDATA", EvidenceLevel.B, new DateOnly(2025, 12, 9), null);

        var merged = FindingMerger.Merge([a, b, c]);

        Assert.Equal([a.Function!.From, b.Function!.From], merged.Select(f => f.Function!.From));
    }
}
