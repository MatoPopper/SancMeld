using System.Text.Json;
using Xunit;

namespace SancMeld.Connectors.Rpvs.Tests;

public sealed class RpvsRecordParserTests
{
    private static RpvsPage Sample()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "fixtures", "rpvs", "partneri-sample.json")));
        return RpvsRecordParser.Parse(doc.RootElement);
    }

    [Fact]
    public void ThePartnerKeepsItsCurrentCompanyName()
    {
        var partner = Sample().Partners[0];

        Assert.Equal(900001, partner.Id);
        Assert.Equal(800001, partner.FileNumber);
        Assert.Equal("Example Trade Holding s.r.o.", partner.Company!.Name);
        Assert.Equal("99000001", partner.Company.Ico);
    }

    [Fact]
    public void BirthDatesStayOnTheirCalendarDay()
    {
        // Midnight in Bratislava is the evening before in UTC; the birthday must not move.
        var owner = Sample().Partners[0].Owners[0];

        Assert.Equal("Ivan Testovsky", owner.FullName);
        Assert.Equal(new DateOnly(1970, 6, 15), owner.BirthDate);
        Assert.Equal("Ruská federácia", owner.Nationality);
        Assert.Equal(new DateOnly(2021, 3, 31), owner.End);
    }

    [Fact]
    public void AnOwnerWithoutABirthDateIsStillRead()
    {
        var owner = Sample().Partners[0].Owners[2];

        Assert.Null(owner.BirthDate);
        Assert.True(owner.IsPublicOfficial);
        Assert.Null(owner.End);
    }

    [Fact]
    public void ASoleTraderIsNamedByTheirName()
    {
        var partner = Sample().Partners[1];

        Assert.Equal("Peter Vzorovy", partner.Company!.Name);
        Assert.Equal("FyzickaOsobaPodnikatel", partner.Company.Form);
        Assert.Empty(partner.Owners);
    }

    [Fact]
    public void TheNextLinkIsKept()
    {
        Assert.EndsWith("$skiptoken=Id-900002", Sample().NextLink, StringComparison.Ordinal);
    }
}
