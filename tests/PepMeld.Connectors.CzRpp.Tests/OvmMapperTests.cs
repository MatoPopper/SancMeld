using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Connectors.CzRpp.Tests;

public sealed class OvmMapperTests
{
    private static IReadOnlyList<ParsedPerson> Map()
    {
        using var file = File.OpenRead("fixtures/cz-rpp/ovm.json");
        return OvmMapper.Map(file);
    }

    private static (ParsedPerson Person, ParsedFunction Function) Head(string name)
    {
        var person = Assert.Single(Map(), p => p.DisplayName == name);
        return (person, Assert.Single(person.Functions));
    }

    [Fact]
    public void OnlyTheHeadsOfTheAuthoritiesTheGuidanceNamesAreRead()
    {
        var names = Map().Select(p => p.DisplayName).Order(StringComparer.Ordinal).ToList();

        // Not the mayor of a small municipality, not the head of an authority that has
        // ended, and nobody for a ministry the register gives no head.
        Assert.Equal(
            ["Alena Schillerová", "Bohuslav Svoboda", "Iveta Kočí Palkovská", "Jan Grolich", "Lukáš Seifert", "Marek Rojíček", "Markéta Vaňková", "Terezie Radoměřská"],
            names);
    }

    [Theory]
    [InlineData("Jan Grolich", PepCategory.RegionalLeader, "hejtman", "Jihomoravský kraj")]
    [InlineData("Bohuslav Svoboda", PepCategory.RegionalLeader, "primátor hlavního města Prahy", "Hlavní Město Praha")]
    [InlineData("Markéta Vaňková", PepCategory.RegionalLeader, "primátor", "Statutární město Brno")]
    [InlineData("Lukáš Seifert", PepCategory.RegionalLeader, "starosta obce s rozšířenou působností", "Město Kutná Hora")]
    [InlineData("Terezie Radoměřská", PepCategory.RegionalLeader, "starosta městské části", "Městská část Praha 1")]
    [InlineData("Alena Schillerová", PepCategory.GovernmentMember, "ministr", "Ministerstvo financí")]
    [InlineData("Marek Rojíček", PepCategory.GovernmentMember, "vedoucí ústředního orgánu státní správy", "Český statistický úřad")]
    public void EachHeadHasTheOfficeOfTheirAuthority(string name, PepCategory category, string role, string institution)
    {
        var (person, function) = Head(name);

        Assert.Equal(category, function.Category);
        Assert.Equal(role, function.Role);
        Assert.Equal(institution, function.Institution);
        Assert.Equal(Country.CZ, function.Country);

        // The register knows neither the birthday nor when the office began.
        Assert.Null(person.BirthDate);
        Assert.Null(function.From);
        Assert.Null(function.To);
        Assert.NotNull(function.DateNote);
    }

    [Fact]
    public void ADoubleSurnameStaysTogetherAndCapitalsAreUndone()
    {
        var (person, _) = Head("Iveta Kočí Palkovská");
        Assert.Equal("Iveta", person.GivenName);
        Assert.Equal("Kočí Palkovská", person.FamilyName);
    }

    [Fact]
    public void TheKeyIsTheAuthorityAndTheNameSoANewHeadIsANewPerson()
    {
        var (person, function) = Head("Markéta Vaňková");
        Assert.StartsWith("v1:44992785:", person.SourceKey, StringComparison.Ordinal);
        Assert.Equal("ovm:44992785", function.SourceKey);
        Assert.Equal(OvmMapper.CatalogueUrl + "44992785", person.SourceUrl);
    }
}
