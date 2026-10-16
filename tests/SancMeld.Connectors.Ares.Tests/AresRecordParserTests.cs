using System.Text.Json;
using SancMeld.Core.Relations;
using Xunit;

namespace SancMeld.Connectors.Ares.Tests;

public sealed class AresRecordParserTests
{
    private static AresCompany Sample()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "ares", "vr-sample.json")));
        return AresRecordParser.Parse(doc.RootElement)!;
    }

    [Fact]
    public void TheCurrentNameAndCountryAreRead()
    {
        var company = Sample();

        Assert.Equal("01234567", company.Ico);
        Assert.Equal("Sample Retail CZ s.r.o.", company.Name);
        Assert.Equal("CZ", company.CountryIso2);
    }

    [Fact]
    public void StintsOfOneShareholderMergeIntoOneTie()
    {
        // Two stints, 2019–2023 and from 2026: one tie since 2019, still current.
        var andrey = Assert.Single(Sample().People, p => p.Surname == "SHNAYDER" && p.GivenName == "ANDREY");

        Assert.Equal(RelationKind.OwnerOf, andrey.Role);
        Assert.Equal(new DateOnly(2019, 1, 22), andrey.Start);
        Assert.Null(andrey.End);
        Assert.Equal(15m, andrey.SharePercent);
    }

    [Fact]
    public void AnEndedShareKeepsItsEndAndTheLastShareItHad()
    {
        // 61,5 % from 2019, then 76 % until 2023: the last one, whatever the array order.
        var sergey = Assert.Single(Sample().People, p => p.GivenName == "SERGEY");

        Assert.Equal(new DateOnly(2023, 1, 5), sergey.End);
        Assert.Equal(76m, sergey.SharePercent);
    }

    [Fact]
    public void ASemicolonIsADecimalSeparator()
    {
        using var doc = JsonDocument.Parse("""
            { "zaznamy": [ { "primarniZaznam": true, "ico": [ { "hodnota": "1" } ], "obchodniJmeno": [ { "hodnota": "X" } ],
              "spolecnici": [ { "spolecnik": [ { "podil": [ { "velikostPodilu": { "typObnos": "PROCENTA", "hodnota": "61;5" } } ],
                "osoba": { "fyzickaOsoba": { "jmeno": "A", "prijmeni": "B", "datumNarozeni": "1970-01-01" } } } ] } ] } ] }
            """);

        Assert.Equal(61.5m, AresRecordParser.Parse(doc.RootElement)!.People.Single().SharePercent);
    }

    [Fact]
    public void StatutoryMembersAndCorporateHoldersAreRead()
    {
        var company = Sample();

        var director = Assert.Single(company.People, p => p.Role == RelationKind.DirectorOf);
        Assert.Equal("jednatel", director.Function);

        var holder = Assert.Single(company.Holders);
        Assert.Equal("Sample Holding Ltd", holder.Name);
        Assert.Equal("CY", holder.CountryIso2);
        Assert.Null(holder.Ico);
        Assert.Equal(3m, holder.SharePercent);
    }
}
