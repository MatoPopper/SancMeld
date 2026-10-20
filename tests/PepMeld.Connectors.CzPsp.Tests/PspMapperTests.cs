using System.IO.Compression;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Connectors.CzPsp.Tests;

public class PspMapperTests
{
    private static IReadOnlyList<ParsedPerson> Map()
    {
        using var stream = FixtureArchive.Zip();
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
        return PspMapper.Map(archive);
    }

    private static ParsedPerson Person(string key) => Map().Single(p => p.SourceKey == key);

    /// <summary>
    /// Eleven persons in the file, ten with a function this product cares about. The
    /// eleventh (5536) only ever sat in a body of a type the mapper does not read.
    /// </summary>
    [Fact]
    public void OnlyPersonsWithARelevantFunctionAreReturned()
    {
        var persons = Map();

        Assert.Equal(10, persons.Count);
        Assert.DoesNotContain(persons, p => p.SourceKey == "5536");
        Assert.All(persons, p => Assert.NotEmpty(p.Functions));
        Assert.All(persons, p => Assert.All(p.Functions, f => Assert.Equal(Country.CZ, f.Country)));
    }

    [Fact]
    public void ADeputyIsReadWithTitlesApartAndTheDateOfBirth()
    {
        var babis = Person("6150");

        Assert.Equal("Andrej", babis.GivenName);
        Assert.Equal("Babiš", babis.FamilyName);
        Assert.Equal("Ing.", babis.TitleBefore);
        Assert.Null(babis.TitleAfter);
        Assert.Equal("Andrej Babiš", babis.DisplayName);
        Assert.Equal(new DateOnly(1954, 9, 2), babis.BirthDate);
        Assert.Null(babis.DeathDate);
        Assert.Equal("https://www.psp.cz/sqw/detail.sqw?id=6150", babis.SourceUrl);
    }

    /// <summary>Four terms as a deputy, each its own function with the term's dates; the current one open.</summary>
    [Fact]
    public void EachTermInTheChamberIsOneFunction()
    {
        var terms = Person("6150").Functions.Where(f => f.Role == "poslanec").OrderBy(f => f.From).ToList();

        Assert.Equal(4, terms.Count);
        Assert.All(terms, f => Assert.Equal(PepCategory.MemberOfParliament, f.Category));
        Assert.Equal(new DateOnly(2013, 10, 26), terms[0].From);
        Assert.Equal(new DateOnly(2017, 10, 26), terms[0].To);
        Assert.Equal("Poslanecká sněmovna Parlamentu ČR (PSP7)", terms[0].Institution);
        Assert.Equal(new DateOnly(2025, 10, 4), terms[3].From);
        Assert.Null(terms[3].To);
    }

    /// <summary>In the Government only offices count, so the same person is "Ministr financí", not "člen vlády".</summary>
    [Fact]
    public void GovernmentOfficesAreReadAndGovernmentMembershipsAreNot()
    {
        var government = Person("6150").Functions.Where(f => f.Category == PepCategory.GovernmentMember).ToList();

        Assert.DoesNotContain(government, f => f.Role == "člen vlády");
        Assert.Contains(government, f => f.Role == "Ministr financí" && f.From == new DateOnly(2014, 1, 29) && f.To == new DateOnly(2017, 5, 24));
        Assert.Equal(3, government.Count(f => f.Role == "Předseda vlády"));
        Assert.Contains(government, f => f.Role == "Předseda vlády" && f.From == new DateOnly(2025, 12, 9) && f.To is null);
        Assert.All(government, f => Assert.Equal("Vláda České republiky", f.Institution));
    }

    /// <summary>The mandate dates in od_f/do_f are the legal ones and win over the record's own.</summary>
    [Fact]
    public void MandateDatesWinOverRecordDates()
    {
        var klaus = Person("263").Functions.Where(f => f.Category == PepCategory.HeadOfState).OrderBy(f => f.From).ToList();

        Assert.Equal(2, klaus.Count);
        Assert.Equal(new DateOnly(2003, 3, 7), klaus[0].From);
        Assert.Equal(new DateOnly(2008, 3, 7), klaus[0].To);
        Assert.Equal(new DateOnly(2008, 3, 7), klaus[1].From);
        Assert.Equal(new DateOnly(2013, 3, 7), klaus[1].To);

        var havel = Person("4730");
        Assert.Equal(new DateOnly(2011, 12, 18), havel.DeathDate);
        var presidency = Assert.Single(havel.Functions, f => f.Category == PepCategory.HeadOfState);
        Assert.Equal(new DateOnly(1998, 2, 2), presidency.From);
        Assert.Equal(new DateOnly(2003, 2, 2), presidency.To);
        Assert.Equal("Prezident", presidency.Role);
    }

    [Fact]
    public void TheCurrentPresidentHasAnOpenOffice()
    {
        var office = Assert.Single(Person("6867").Functions);

        Assert.Equal(PepCategory.HeadOfState, office.Category);
        Assert.Equal(new DateOnly(2023, 3, 9), office.From);
        Assert.Null(office.To);
    }

    /// <summary>1 January 1900 is the publisher's "unknown", not a birthday: the namesake senator has none.</summary>
    [Fact]
    public void TheUnknownBirthdaySentinelBecomesNull()
    {
        var namesake = Person("6870");

        Assert.Equal("Petr Fiala", namesake.DisplayName);
        Assert.Null(namesake.BirthDate);
        Assert.Equal(2, namesake.Functions.Count);
        Assert.All(namesake.Functions, f => Assert.Equal("senátor", f.Role));

        var primeMinister = Person("6074");
        Assert.Equal(new DateOnly(1964, 9, 1), primeMinister.BirthDate);
        Assert.Equal("Ph.D., LL.M.", primeMinister.TitleAfter);
    }

    /// <summary>Chamber offices are only the speaker and deputy speakers; committee chairs and verifiers are not read.</summary>
    [Fact]
    public void OnlyChairOfficesAreReadInTheChambers()
    {
        var fiala = Person("6074");

        Assert.Contains(fiala.Functions, f => f.Role == "1. místopředseda" && f.Institution == "Poslanecká sněmovna Parlamentu ČR (PSP8)");
        Assert.DoesNotContain(fiala.Functions, f => string.IsNullOrWhiteSpace(f.Role));

        var zeman = Person("253");
        Assert.Contains(zeman.Functions, f => f.Role.StartsWith("Předseda Poslanecké sněmovny", StringComparison.Ordinal) && f.From == new DateOnly(1996, 6, 27));
    }

    /// <summary>
    /// Eight records in the file for three mandates: the Senate is one body per two-year
    /// period. The 2016 mandate is recorded open in one body and ended in two; the senator
    /// was re-elected in 2022, so the ended records are right.
    /// </summary>
    [Fact]
    public void ASenateMandateIsOneFunctionHoweverManyBodiesRecordIt()
    {
        var terms = Person("4775").Functions.Where(f => f.Role == "senátor").OrderBy(f => f.From).ToList();

        Assert.Equal(3, terms.Count);
        Assert.All(terms, f => Assert.Equal("Senát Parlamentu ČR", f.Institution));
        Assert.Equal(new DateOnly(2010, 10, 23), terms[0].From);
        Assert.Equal(new DateOnly(2016, 10, 23), terms[0].To);
        Assert.Equal(new DateOnly(2016, 10, 15), terms[1].From);
        Assert.Equal(new DateOnly(2022, 10, 15), terms[1].To);
        Assert.Equal(new DateOnly(2022, 10, 1), terms[2].From);
        Assert.Null(terms[2].To);
    }

    [Fact]
    public void FunctionKeysAreUniqueWithinAPerson()
    {
        foreach (var person in Map())
        {
            Assert.Equal(person.Functions.Count, person.Functions.Select(f => f.SourceKey).Distinct().Count());
        }
    }

    [Theory]
    [InlineData("1|Ing.|Bachna|Petr||08.01.1948|M|||", 9)]
    [InlineData("a|b\\|c|d|", 3)]
    [InlineData("x|y", 2)]
    public void UnlReader_SplitsOnPipesAndHonoursEscapes(string line, int fields)
        => Assert.Equal(fields, UnlReader.Split(line).Length);

    [Fact]
    public void UnlReader_KeepsAnEscapedPipeInTheValue()
        => Assert.Equal("b|c", UnlReader.Split("a|b\\|c|d|")[1]);
}
