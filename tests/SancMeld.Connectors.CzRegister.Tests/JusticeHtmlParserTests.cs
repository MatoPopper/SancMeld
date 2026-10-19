using Xunit;

namespace SancMeld.Connectors.CzRegister.Tests;

public sealed class JusticeHtmlParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "czregister", name));

    [Fact]
    public void ASearchPageGivesItsRowsWithBirthDateAddressRoleAndIco()
    {
        var page = JusticeHtmlParser.Parse(Fixture("justice-person-search.html"));

        Assert.True(page.Truncated);
        Assert.False(page.Captcha);
        Assert.Equal(2, page.Rows.Count);

        var first = page.Rows[0];
        Assert.Equal("Testovan Prikladov", first.Name);
        Assert.Equal(new DateOnly(1948, 6, 30), first.BirthDate);
        Assert.Equal("Moskva, Vymyšlená 1, Ruská federace", first.Address);
        Assert.Equal("RU", first.CountryIso2);
        Assert.Equal("společník", first.Role);
        Assert.Equal("Vzorová firma s.r.o. v likvidaci", first.Company);
        Assert.Equal("12345678", first.Ico);
        Assert.True(first.CompanyDeleted);
        Assert.Equal("293640", first.SubjectId);

        var second = page.Rows[1];
        Assert.Equal("Jana Ukázková", second.Name);
        Assert.Equal(new DateOnly(1968, 8, 6), second.BirthDate);
        Assert.Equal("CZ", second.CountryIso2);
        Assert.Equal("člen dozorčí rady", second.Role);
        Assert.Equal("24794392", second.Ico);
        Assert.False(second.CompanyDeleted);
    }

    [Theory]
    [InlineData("31. prosince 1976", 1976, 12, 31)]
    [InlineData("1. ledna 1900", 1900, 1, 1)]
    [InlineData("6. srpna 1968", 1968, 8, 6)]
    public void CzechDatesAreRead(string text, int y, int m, int d)
    {
        Assert.Equal(new DateOnly(y, m, d), JusticeHtmlParser.Date(text));
    }

    [Fact]
    public void AnEmptyOrForeignPageGivesNoRows()
    {
        Assert.Empty(JusticeHtmlParser.Parse("<html><body>nothing here</body></html>").Rows);
    }

    [Theory]
    [InlineData("Rupperswil, Dorfstrasse 17, CH - 5102, Švýcarská konfederace", "CH")]
    [InlineData("Bad Salzuflen, Wasserfuhr 61, 32108, Spolková republika Německo", "DE")]
    [InlineData("Brno, Veveří 1", "CZ")]
    public void TheCountryIsTheLastPartOfTheAddress(string address, string iso2)
    {
        Assert.Equal(iso2, JusticeHtmlParser.Country(address));
    }
}
