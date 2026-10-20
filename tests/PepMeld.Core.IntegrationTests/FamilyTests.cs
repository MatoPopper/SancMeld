using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PepMeld.Connectors.CzPsp;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// The family stage end to end: a research file for a PEP of the fixture (Petr Pavel, born
/// 1 November 1961) is imported, then people are checked against it. Names are made unique
/// per test run so the ties of one run do not answer another's checks.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class FamilyTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string tag = Guid.NewGuid().ToString("N")[..6];
    private readonly string snapshots = TestServices.TempDirectory();

    private string Spouse => $"Eva Pavlova{tag}";
    private string Child => $"Jan Pavel{tag}";
    private string Sibling => $"Marie Pavlova{tag}";
    private string Former => $"Hana Pavlova{tag}";
    private string Minor => $"Ema Pavlova{tag}";

    private sealed class FixedVerifier(QuoteCheck answer) : IQuoteVerifier
    {
        public Task<QuoteCheck> CheckAsync(string url, string quote, CancellationToken ct) => Task.FromResult(answer);
    }

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await db.Sources.Where(s => s.Code == CzPspConnector.Code).ExecuteUpdateAsync(s => s.SetProperty(x => x.MinExpectedPersons, 5));
        if (!await db.Persons.AnyAsync(p => p.SourceId == WellKnownIds.CzPspSource))
        {
            await TestServices.Importer(db, new FixtureConnector(), TestServices.Options(snapshots)).RunAsync(CzPspConnector.Code, force: true, CancellationToken.None);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private string File(string extra = "") => $$"""
        {
          "pep": { "name": "Petr Pavel", "dateOfBirth": "1961-11-01", "country": "CZ" },
          "agent": { "startedAt": "2026-10-20T12:00:00Z", "finishedAt": "2026-10-20T12:01:30Z", "searches": 5, "pagesOpened": 6 },
          "relatives": [
            { "relation": "Spouse", "name": "{{Spouse}}", "status": "current", "confidence": "high",
              "evidence": [ { "url": "https://example.org/a", "quote": "jeho manželka Eva" } ] },
            { "relation": "Child", "name": "{{Child}}", "dateOfBirth": "1990-03-04", "status": "current", "confidence": "high",
              "evidence": [ { "url": "https://example.org/b", "quote": "syn Jan" } ] },
            { "relation": "Sibling", "name": "{{Sibling}}", "status": "current", "confidence": "medium",
              "evidence": [ { "url": "https://example.org/c", "quote": "sestra Marie" } ] },
            { "relation": "Spouse", "name": "{{Former}}", "status": "former", "confidence": "high",
              "evidence": [ { "url": "https://example.org/d", "quote": "bývalá manželka Hana" } ] },
            { "relation": "Child", "name": "{{Minor}}", "dateOfBirth": "2015-01-01", "birthYear": 2015, "isMinor": true, "status": "current",
              "evidence": [ { "url": "https://example.org/e", "quote": "dcera Ema" } ] },
            { "relation": "Child", "name": "No Evidence {{tag}}", "status": "current", "evidence": [] },
            { "relation": "Cousin", "name": "Bratranec {{tag}}", "status": "current",
              "evidence": [ { "url": "https://example.org/f", "quote": "bratranec" } ] }
            {{extra}}
          ],
          "notes": "test"
        }
        """;

    private async Task<FamilyImportOutcome> ImportAsync(IQuoteVerifier? verifier = null)
    {
        await using var db = fixture.CreateContext();
        return await new FamilyImportService(db, verifier ?? new FixedVerifier(QuoteCheck.Exact), TimeProvider.System, NullLogger<FamilyImportService>.Instance)
            .ImportAsync(File(), "test-agent", CancellationToken.None);
    }

    private async Task<CheckView> CheckAsync(string name, DateOnly born)
    {
        var email = $"family-{Guid.NewGuid():N}@example.com";
        await using var db = fixture.CreateContext();
        var created = await TestServices.Checks(db).CreateAsync(email, new NewCheck(name, born, null, null, null, null, null), CancellationToken.None);

        await using var runnerDb = fixture.CreateContext();
        var options = TestServices.Options(snapshots);
        await new CheckRunner(runnerDb,
            [new ListsStage(new PepListMatcher(runnerDb, options), new SourceQueryService(runnerDb), options, TimeProvider.System), new FamilyStage(runnerDb, options)],
            options, TimeProvider.System, NullLogger<CheckRunner>.Instance).RunAsync(created.Check!.CheckId, CancellationToken.None);

        await using var readDb = fixture.CreateContext();
        return (await TestServices.Checks(readDb).GetAsync(email, created.Check.CheckId, CancellationToken.None))!;
    }

    [Fact]
    public async Task TheImportKeepsWhatHasEvidenceAndStripsAMinorsBirth()
    {
        var outcome = await ImportAsync();

        Assert.Equal(5, outcome.Imported);
        Assert.Equal(2, outcome.Refused);
        Assert.Contains(outcome.Refusals, r => r.Contains("no evidence", StringComparison.Ordinal));
        Assert.Contains(outcome.Refusals, r => r.Contains("Cousin", StringComparison.Ordinal));
        Assert.Equal(90, outcome.DurationSeconds);

        await using var db = fixture.CreateContext();
        var minor = await db.FamilyLinks.SingleAsync(l => l.RelativeName == Minor);
        Assert.True(minor.IsMinor);
        Assert.Null(minor.RelativeBirthDate);
        Assert.Null(minor.RelativeBirthYear);

        var all = await db.FamilyLinks.Where(l => l.RunId == outcome.RunId).ToListAsync();
        Assert.All(all, l => Assert.Equal(EvidenceLevel.D, l.Level));
        Assert.All(all, l => Assert.Equal(FamilyReviewStatus.Candidate, l.ReviewStatus));
        Assert.All(all, l => Assert.Contains("\"quoteCheck\": \"Exact\"", l.EvidenceJson, StringComparison.Ordinal));

        // A second run updates the same ties instead of adding them again.
        var again = await ImportAsync();
        Assert.Equal(0, again.Imported);
        Assert.Equal(5, again.Updated);
    }

    [Fact]
    public async Task ARelativeWhoseQuotesAreNotOnTheirPagesIsRefused()
    {
        var outcome = await ImportAsync(new FixedVerifier(QuoteCheck.NotFound));

        Assert.Equal(0, outcome.Imported + outcome.Updated);
        Assert.Contains(outcome.Refusals, r => r.Contains("no quote was found", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnreadablePageNeitherConfirmsNorRefuses()
    {
        var outcome = await ImportAsync(new FixedVerifier(QuoteCheck.Unreachable));

        Assert.Equal(5, outcome.Imported + outcome.Updated);
        Assert.Equal(0, outcome.QuotesFound);
    }

    [Fact]
    public async Task TheSpouseIsAFamilyMemberAtLevelDWithWhatToVerify()
    {
        await ImportAsync();
        var check = await CheckAsync(Spouse, new DateOnly(1965, 5, 5));

        Assert.Equal(CheckOutcome.FamilyMember, check.Outcome);
        var finding = Assert.Single(check.Stages.Single(s => s.Stage == CheckStage.Family).Findings);
        Assert.Equal(FindingKind.FamilyMember, finding.Kind);
        Assert.Equal(EvidenceLevel.D, finding.Level);
        Assert.Equal(FamilyRelation.Spouse, finding.Relation);
        Assert.Equal("Petr Pavel", finding.RelatedTo);
        Assert.False(finding.Person.DateOfBirthMatched);
        Assert.NotNull(finding.ToVerify);
        Assert.Contains("osoba blízká", finding.Explanation, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AChildWithARecordedBirthdayIsMatchedOnIt()
    {
        await ImportAsync();

        var right = await CheckAsync(Child, new DateOnly(1990, 3, 4));
        Assert.Equal(CheckOutcome.FamilyMember, right.Outcome);
        Assert.True(right.Stages.Single(s => s.Stage == CheckStage.Family).Findings.Single().Person.DateOfBirthMatched);

        var wrong = await CheckAsync(Child, new DateOnly(1990, 3, 5));
        Assert.Equal(CheckOutcome.NoMatch, wrong.Outcome);
    }

    [Fact]
    public async Task AFormerSpouseIsNoFamilyMember()
    {
        await ImportAsync();
        var check = await CheckAsync(Former, new DateOnly(1966, 1, 1));

        Assert.Equal(CheckOutcome.NoMatch, check.Outcome);
        Assert.Empty(check.Stages.Single(s => s.Stage == CheckStage.Family).Findings);
    }

    [Fact]
    public async Task ThePepCheckedListsTheirKnownRelatives()
    {
        await ImportAsync();
        var check = await CheckAsync("Petr Pavel", new DateOnly(1961, 11, 1));

        Assert.Equal(CheckOutcome.Pep, check.Outcome);
        var family = check.Stages.Single(s => s.Stage == CheckStage.Family);
        Assert.Equal(StageStatus.Completed, family.Status);

        var mine = family.Findings.Where(f => f.Person.Name.EndsWith(tag, StringComparison.Ordinal)).ToList();
        Assert.All(mine, f => Assert.Equal(FindingKind.KnownRelative, f.Kind));
        Assert.Contains(mine, f => f.Person.Name == Spouse);
        Assert.Contains(mine, f => f.Person.Name == Sibling && f.LegallyRelevant == true);
        Assert.Contains(mine, f => f.Person.Name == Minor && f.Explanation.Contains("maloletý", StringComparison.Ordinal));
        Assert.DoesNotContain(mine, f => f.Person.Name == Former);
        Assert.Contains("dohľadané pre", family.Note, StringComparison.Ordinal);
    }
}
