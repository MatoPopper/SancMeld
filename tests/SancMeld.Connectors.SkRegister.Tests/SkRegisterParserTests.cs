using System.Text.Json;
using SancMeld.Core.Relations;
using Xunit;

namespace SancMeld.Connectors.SkRegister.Tests;

public sealed class SkRegisterParserTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "skregister", name));

    [Fact]
    public void APersonSearchPageGivesItsRowsAndTheTotal()
    {
        var html = Fixture("orsr-person-search.html");

        var rows = OrsrHtmlParser.PersonRows(html);

        Assert.Equal(2, rows.Count);
        Assert.Equal(new OrsrPersonRow("Testovan Prikladov", "Vzorová firma s. r. o.", "123456", "2"), rows[0]);
        Assert.Equal("Ukážka & spol., a. s.", rows[1].Company);
        Assert.Equal(43, OrsrHtmlParser.Total(html));
    }

    [Fact]
    public void AnExtractGivesItsIco()
    {
        Assert.Equal("12345678", OrsrHtmlParser.Ico(Fixture("orsr-extract.html")));
        Assert.Null(OrsrHtmlParser.Ico("<html>no number here</html>"));
    }

    [Fact]
    public void AnRpoEntityGivesItsPeopleWithRolesAndCountries()
    {
        using var doc = JsonDocument.Parse(Fixture("rpo-entity.json"));

        var company = RpoRecordParser.ParseEntity(doc.RootElement)!;

        Assert.Equal("12345678", company.Ico);
        Assert.Equal("Vzorová firma s. r. o.", company.Name);

        var owner = Assert.Single(company.People, p => p.Role == RelationKind.OwnerOf);
        Assert.Equal("Testovan Prikladov", owner.FullName);
        Assert.Equal("RU", owner.CountryIso2);
        Assert.Equal("Moskva, Ruská federácia", owner.Place);
        Assert.Equal(new DateOnly(2019, 8, 6), owner.Start);

        var director = Assert.Single(company.People, p => p.Role == RelationKind.DirectorOf);
        Assert.Equal("Konateľ", director.Function);
        Assert.Equal("SK", director.CountryIso2);

        // A company as shareholder is kept as its country only.
        Assert.Equal(["CY"], company.CorporateOwnerCountries);
    }

    [Fact]
    public void ASearchHitCarriesFormerNames()
    {
        using var doc = JsonDocument.Parse($$"""{ "results": [ {{Fixture("rpo-entity.json")}} ] }""");

        var hit = Assert.Single(RpoRecordParser.ParseSearch(doc.RootElement));

        Assert.Equal(10000001, hit.Id);
        Assert.Equal(["Stará firma s. r. o.", "Vzorová firma s. r. o."], hit.Names);
    }

    [Theory]
    [InlineData("Aleksandr Anatolevich SURKOV", "Surkov", "Aleksandr")]
    [InlineData("SURKOV Aleksandr Anatolevich", "Surkov", "Aleksandr")]
    [InlineData("Olga Korchagina", "Korchagina", "Olga")]
    public void APersonIsFiledBySurnameAndGivenName(string spelling, string surname, string given)
    {
        var person = Assert.Single(ListingNames.Persons([spelling]));

        Assert.Equal(surname, person.Surname);
        Assert.Equal(given, person.GivenName);
    }

    [Fact]
    public void CyrillicSpellingsAreAskedInLatin()
    {
        var latin = ListingNames.Latin(["Ольга Александровна КОРЧАГИНА"]);

        Assert.NotEmpty(latin);
        Assert.All(latin, l => Assert.DoesNotContain(l, c => c is >= 'Ѐ' and <= 'ӿ'));
        Assert.Contains(ListingNames.Persons(latin), p => p.Surname.StartsWith("Korchagin", StringComparison.OrdinalIgnoreCase));
    }

    [Theory]
    [InlineData("Wildberries Bank LLC", new[] { "wildberries" })]
    [InlineData("Limited Liability Company \"Finist\"", new[] { "finist" })]
    [InlineData("PJSC Bank Sinara", new[] { "sinara" })]
    [InlineData("Digital Trust Bank", new string[0])]
    public void ACompanyIsKnownByItsDistinctiveWords(string name, string[] words)
    {
        Assert.Equal(words, ListingNames.Distinctive(name));
    }

    [Fact]
    public void AWordMustBeWholeInTheSlovakName()
    {
        Assert.True(ListingNames.ContainsAllWords("Wildberries s. r. o.", ["wildberries"]));
        Assert.True(ListingNames.ContainsAllWords("SINARA TRANSPORTATION EAST s. r. o.", ["sinara"]));
        Assert.False(ListingNames.ContainsAllWords("Wildberrieson s. r. o.", ["wildberries"]));
        Assert.False(ListingNames.ContainsAllWords("Slovak Trade s. r. o.", ["ak"]));
    }
}
