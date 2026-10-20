using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PepMeld.Connectors.Wikidata;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// Ties from Wikidata beside the agent's: one tie found by both is one row with both
/// sources' evidence, the better level and the date of birth whichever had it; the order
/// in which the two arrive does not matter.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WikidataFamilyTests(DatabaseFixture fixture)
{
    private readonly string tag = Guid.NewGuid().ToString("N")[..6];

    private sealed class Found : IQuoteVerifier
    {
        public Task<QuoteCheck> CheckAsync(string url, string quote, CancellationToken ct) => Task.FromResult(QuoteCheck.Exact);
    }

    private string Pep => $"Robert Testfico{tag}";

    private WikidataFamilySync Sync(PepMeldDbContext db) =>
        new(new NoHttp(), Options.Create(new WikidataPositionsOptions()), db, TimeProvider.System, NullLogger<WikidataFamilySync>.Instance);

    private sealed class NoHttp : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No HTTP in this test.");
    }

    private WikidataTie Wife(DateOnly? born = null) =>
        new("Q1", Pep, new DateOnly(1964, 9, 15), Country.SK, FamilyRelation.Spouse, "Q2", $"Svetlana Testficova{tag}", born ?? new DateOnly(1964, 9, 6), false, false);

    private string AgentFile() => $$"""
        {
          "pep": { "name": "{{Pep}}", "dateOfBirth": "1964-09-15", "country": "SK" },
          "agent": { "startedAt": "2026-10-20T12:00:00Z", "finishedAt": "2026-10-20T12:01:40Z", "searches": 5, "pagesOpened": 7 },
          "relatives": [
            { "relation": "Spouse", "name": "Svetlana Testficova{{tag}} (rod. Svobodova)", "status": "current", "confidence": "medium",
              "evidence": [ { "url": "https://sk.wikipedia.org/wiki/X", "quote": "manželka Svetlana" } ] }
          ]
        }
        """;

    private async Task ImportAgentAsync()
    {
        await using var db = fixture.CreateContext();
        await new FamilyImportService(db, new Found(), TimeProvider.System, NullLogger<FamilyImportService>.Instance)
            .ImportAsync(AgentFile(), "test-agent", CancellationToken.None);
    }

    private async Task<FamilyLink> TheWifeAsync()
    {
        await using var db = fixture.CreateContext();
        return await db.FamilyLinks.AsNoTracking().SingleAsync(l => l.PepName == Pep);
    }

    [Fact]
    public async Task AgentFirstThenWikidataIsOneTieWithBothAndTheBetterLevel()
    {
        await ImportAgentAsync();

        await using (var db = fixture.CreateContext())
        {
            var (inserted, updated) = await Sync(db).WriteAsync([Wife()], CancellationToken.None);
            Assert.Equal((0, 1), (inserted, updated));
        }

        var wife = await TheWifeAsync();
        Assert.Equal(EvidenceLevel.B, wife.Level);
        Assert.Equal(new DateOnly(1964, 9, 6), wife.RelativeBirthDate);
        Assert.Equal($"Svetlana Testficova{tag}", wife.RelativeName);
        Assert.Contains("sk.wikipedia.org", wife.EvidenceJson, StringComparison.Ordinal);
        Assert.Contains("wikidata.org/wiki/Q1", wife.EvidenceJson, StringComparison.Ordinal);
    }

    [Fact]
    public async Task WikidataFirstThenTheAgentKeepsWikidatasDateAndLevel()
    {
        await using (var db = fixture.CreateContext())
        {
            await Sync(db).WriteAsync([Wife()], CancellationToken.None);
        }

        await ImportAgentAsync();

        var wife = await TheWifeAsync();
        Assert.Equal(EvidenceLevel.B, wife.Level);
        Assert.Equal(new DateOnly(1964, 9, 6), wife.RelativeBirthDate);
        Assert.Equal($"Svetlana Testficova{tag}", wife.RelativeName);
        Assert.Contains("sk.wikipedia.org", wife.EvidenceJson, StringComparison.Ordinal);
        Assert.Contains("wikidata.org/wiki/Q1", wife.EvidenceJson, StringComparison.Ordinal);
        Assert.NotNull(wife.RunId);
    }

    [Fact]
    public async Task ATieOnlyWikidataHasIsNeverNoLongerNamedByAnAgentRun()
    {
        await using (var db = fixture.CreateContext())
        {
            await Sync(db).WriteAsync(
                [new WikidataTie("Q1", Pep, new DateOnly(1964, 9, 15), Country.SK, FamilyRelation.Parent, "Q3", $"Ludovit Testfico{tag}", null, false, true)],
                CancellationToken.None);
        }

        await using var import = fixture.CreateContext();
        var outcome = await new FamilyImportService(import, new Found(), TimeProvider.System, NullLogger<FamilyImportService>.Instance)
            .ImportAsync(AgentFile(), "test-agent", CancellationToken.None);

        Assert.Empty(outcome.NoLongerNamed);

        await using var read = fixture.CreateContext();
        var parent = await read.FamilyLinks.AsNoTracking().SingleAsync(l => l.PepName == Pep && l.Relation == FamilyRelation.Parent);
        Assert.Equal("deceased", parent.Status);
        Assert.Null(parent.RunId);
    }

    [Fact]
    public async Task AMinorFromWikidataKeepsNoBirthday()
    {
        await using (var db = fixture.CreateContext())
        {
            var born = DateOnly.FromDateTime(DateTime.UtcNow).AddYears(-10);
            await Sync(db).WriteAsync(
                [new WikidataTie("Q1", Pep, new DateOnly(1964, 9, 15), Country.SK, FamilyRelation.Child, "Q4", $"Ema Testficova{tag}", born, false, false)],
                CancellationToken.None);
        }

        await using var read = fixture.CreateContext();
        var child = await read.FamilyLinks.AsNoTracking().SingleAsync(l => l.PepName == Pep && l.Relation == FamilyRelation.Child);
        Assert.True(child.IsMinor);
        Assert.Null(child.RelativeBirthDate);
        Assert.Null(child.RelativeBirthYear);
    }

    [Fact]
    public void TheFamilyQueryNamesTheListedPositions()
    {
        var query = WikidataFamilySync.Query(new WikidataPosition { Item = "Q83307", Jurisdiction = "Q213" }, "cs,sk,en", ["Q10", "Q11"]);

        Assert.Contains("VALUES ?pos { wd:Q10 wd:Q11 }", query, StringComparison.Ordinal);
        Assert.DoesNotContain("P279*", query, StringComparison.Ordinal);
        Assert.Contains("ps:P3373", query, StringComparison.Ordinal);
        Assert.Contains("pq:P582", query, StringComparison.Ordinal);
    }
}
