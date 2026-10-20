using Microsoft.EntityFrameworkCore;
using PepMeld.Connectors.CzPsp;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// The whole path, against the database: import the fixture list, start a check, run it,
/// read the answer. One class, sequential, because every test needs the same imported
/// list and the import is the slow part.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ImportAndCheckTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string snapshots = TestServices.TempDirectory();

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();

        // The seeded floor is for the real file; the fixture has ten persons.
        await db.Sources.Where(s => s.Code == CzPspConnector.Code)
            .ExecuteUpdateAsync(s => s.SetProperty(x => x.MinExpectedPersons, 5));

        if (!await db.Persons.AnyAsync(p => p.SourceId == WellKnownIds.CzPspSource))
        {
            var outcome = await TestServices.Importer(db, new FixtureConnector(), TestServices.Options(snapshots))
                .RunAsync(CzPspConnector.Code, force: true, CancellationToken.None);

            Assert.Equal(ImportStatus.Success, outcome.Status);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<CheckView> RunCheckAsync(string email, NewCheck request)
    {
        await using var db = fixture.CreateContext();
        var created = await TestServices.Checks(db).CreateAsync(email, request, CancellationToken.None);
        Assert.Equal(CreateOutcome.Created, created.Outcome);

        await using var runnerDb = fixture.CreateContext();
        await TestServices.Runner(runnerDb, TestServices.Options(snapshots)).RunAsync(created.Check!.CheckId, CancellationToken.None);

        await using var readDb = fixture.CreateContext();
        return (await TestServices.Checks(readDb).GetAsync(email, created.Check.CheckId, CancellationToken.None))!;
    }

    private static string Email() => $"test-{Guid.NewGuid():N}@example.com";

    [Fact]
    public async Task ImportIsIdempotentAndSkipsUnchangedBytes()
    {
        await using var db = fixture.CreateContext();
        var importer = TestServices.Importer(db, new FixtureConnector(), TestServices.Options(snapshots));

        var again = await importer.RunAsync(CzPspConnector.Code, force: false, CancellationToken.None);
        Assert.Equal(ImportStatus.SkippedUnchanged, again.Status);

        var forced = await importer.RunAsync(CzPspConnector.Code, force: true, CancellationToken.None);
        Assert.Equal(ImportStatus.Success, forced.Status);
        Assert.Equal(10, forced.PersonsSeen);
        Assert.Equal(0, forced.PersonsInserted);
        Assert.Equal(0, forced.FunctionsInserted);
        Assert.Equal(0, forced.FunctionsEnded);

        Assert.Equal(10, await db.Persons.CountAsync(p => p.SourceId == WellKnownIds.CzPspSource));
    }

    [Fact]
    public async Task AFileTooSmallIsRefusedAndNothingIsEnded()
    {
        await using var db = fixture.CreateContext();
        await db.Sources.Where(s => s.Code == CzPspConnector.Code).ExecuteUpdateAsync(s => s.SetProperty(x => x.MinExpectedPersons, 1000));

        try
        {
            var outcome = await TestServices.Importer(db, new FixtureConnector { Salt = "small" }, TestServices.Options(snapshots))
                .RunAsync(CzPspConnector.Code, force: true, CancellationToken.None);

            Assert.Equal(ImportStatus.Failed, outcome.Status);
            Assert.Contains("truncated", outcome.Reason);
            Assert.True(await db.Functions.AnyAsync(f => f.ValidTo == null), "open functions must survive a refused import");
        }
        finally
        {
            await db.Sources.Where(s => s.Code == CzPspConnector.Code).ExecuteUpdateAsync(s => s.SetProperty(x => x.MinExpectedPersons, 5));
        }
    }

    [Fact]
    public async Task NameAndDateOfBirthConfirmAPep()
    {
        var check = await RunCheckAsync(Email(), new NewCheck("Ing. Andrej Babiš", new DateOnly(1954, 9, 2), Country.CZ, null, null, "KYC-1", null));

        Assert.Equal(CheckStatus.Completed, check.Status);
        Assert.Equal(CheckOutcome.Pep, check.Outcome);
        Assert.Equal("KYC-1", check.Query.Reference);

        var lists = Assert.Single(check.Stages, s => s.Stage == CheckStage.Lists);
        Assert.Equal(StageStatus.Completed, lists.Status);
        Assert.All(lists.Findings, f => Assert.Equal(EvidenceLevel.A, f.Level));
        Assert.All(lists.Findings, f => Assert.True(f.Person.DateOfBirthMatched));
        Assert.Contains(lists.Findings, f => f.Function!.Role == "Předseda vlády" && f.Function.To is null);
        Assert.Contains(lists.Findings, f => f.Function!.Role == "Ministr financí" && f.MonthsSinceEnd > 100);
        Assert.All(lists.Findings, f => Assert.Equal("§ 4 odst. 5 písm. a) zákona č. 253/2008 Sb.", f.Function!.LegalBasis));
        Assert.All(lists.Findings, f => Assert.Contains("psp.cz", Assert.Single(f.Evidence).Url));

        // The other two stages do not exist yet and say so, rather than failing.
        Assert.Equal(StageStatus.Skipped, Assert.Single(check.Stages, s => s.Stage == CheckStage.Boards).Status);
        Assert.Equal(StageStatus.Skipped, Assert.Single(check.Stages, s => s.Stage == CheckStage.Family).Status);

        Assert.Contains(check.Coverage, c => c.Source == CzPspConnector.Code && c.LastImportedAt is not null && c.Persons == 10);
    }

    [Fact]
    public async Task ADifferentDateOfBirthIsANamesakeAndNoMatch()
    {
        var check = await RunCheckAsync(Email(), new NewCheck("Andrej Babiš", new DateOnly(1954, 9, 3), null, null, null, null, null));

        Assert.Equal(CheckStatus.Completed, check.Status);
        Assert.Equal(CheckOutcome.NoMatch, check.Outcome);
        Assert.Empty(check.Stages.Single(s => s.Stage == CheckStage.Lists).Findings);
        Assert.Contains("CZ_PSP", check.Summary);
    }

    /// <summary>
    /// Two Petr Fialas in the list: the prime minister, with a date of birth, and a
    /// senator the publisher has no birthday for. The first is confirmed (A), the second
    /// stays a candidate (C) with a note on what would settle it, and the outcome follows
    /// the confirmed one.
    /// </summary>
    [Fact]
    public async Task ANamesakeWithoutADateOfBirthIsReportedAsUnverifiedBesideTheConfirmedMatch()
    {
        var check = await RunCheckAsync(Email(), new NewCheck("Petr Fiala", new DateOnly(1964, 9, 1), Country.CZ, null, null, null, null));

        Assert.Equal(CheckOutcome.Pep, check.Outcome);
        var findings = check.Stages.Single(s => s.Stage == CheckStage.Lists).Findings;

        Assert.Contains(findings, f => f.Level == EvidenceLevel.A && f.Function!.Role == "Předseda vlády" && f.Function.To == new DateOnly(2025, 12, 15));
        Assert.Contains(findings, f => f.Level == EvidenceLevel.A && f.Function!.Role == "poslanec" && f.Function.To is null);

        var senator = findings.Where(f => f.Level == EvidenceLevel.C).ToList();
        Assert.Equal(2, senator.Count);
        Assert.All(senator, f => Assert.Equal("senátor", f.Function!.Role));
        Assert.All(senator, f => Assert.False(f.Person.DateOfBirthMatched));
        Assert.All(senator, f => Assert.NotNull(f.ToVerify));
    }

    [Fact]
    public async Task OnlyAnUnverifiableNamesakeIsUnverified()
    {
        // The senator's birthday is unknown, so no date of birth can confirm or exclude him.
        var check = await RunCheckAsync(Email(), new NewCheck("Petr Fiala", new DateOnly(1970, 1, 1), null, null, null, null, null));

        Assert.Equal(CheckOutcome.Unverified, check.Outcome);
        Assert.All(check.Stages.Single(s => s.Stage == CheckStage.Lists).Findings, f => Assert.Equal(EvidenceLevel.C, f.Level));
    }

    [Fact]
    public async Task AnEndedOfficeIsAFormerPepWithTheMonthsSinceTheEnd()
    {
        var check = await RunCheckAsync(Email(), new NewCheck("Miloš Zeman", new DateOnly(1944, 9, 28), null, null, null, null, null));

        Assert.Equal(CheckOutcome.FormerPep, check.Outcome);
        var presidency = check.Stages.Single(s => s.Stage == CheckStage.Lists).Findings.Single(f => f.Function!.Category == PepCategory.HeadOfState);
        Assert.Equal(new DateOnly(2023, 3, 8), presidency.Function!.To);
        Assert.True(presidency.MonthsSinceEnd > 12);
        Assert.Contains("uplynula", presidency.Explanation);
    }

    [Fact]
    public async Task ACountryFilterOnlyMattersForTheFunctionsOfThatCountry()
    {
        var check = await RunCheckAsync(Email(), new NewCheck("Petr Pavel", new DateOnly(1961, 11, 1), Country.SK, null, null, null, null));

        // Nothing Slovak is held: a Czech president is not found under SK.
        Assert.Equal(CheckOutcome.NoMatch, check.Outcome);
        Assert.Contains("nie sú pokryté (SR:", check.Stages.Single(s => s.Stage == CheckStage.Lists).Note);
    }

    [Fact]
    public async Task TheLimitedScopeOnlyRunsWhatWasAsked()
    {
        var check = await RunCheckAsync(Email(), new NewCheck("Petr Pavel", new DateOnly(1961, 11, 1), null, null, null, null, [CheckStage.Lists]));

        Assert.Equal(CheckOutcome.Pep, check.Outcome);
        Assert.Single(check.Stages);
        Assert.Equal([CheckStage.Lists], check.Query.Scope);
    }

    [Fact]
    public async Task OneRunningCheckPerUserIsEnforcedByTheDatabase()
    {
        var email = Email();
        await using var db = fixture.CreateContext();
        var checks = TestServices.Checks(db);
        var request = new NewCheck("Petr Pavel", new DateOnly(1961, 11, 1), null, null, null, null, null);

        var first = await checks.CreateAsync(email, request, CancellationToken.None);
        Assert.Equal(CreateOutcome.Created, first.Outcome);

        var second = await checks.CreateAsync(email, request, CancellationToken.None);
        Assert.Equal(CreateOutcome.AlreadyRunning, second.Outcome);
        Assert.Equal(first.Check!.CheckId, second.Check!.CheckId);

        // Another user is not in the way of the first.
        var other = await checks.CreateAsync(Email(), request, CancellationToken.None);
        Assert.Equal(CreateOutcome.Created, other.Outcome);

        var current = await checks.CurrentAsync(email, CancellationToken.None);
        Assert.Equal(first.Check.CheckId, current!.CheckId);

        // Once run, the slot is free again.
        await TestServices.Runner(db, TestServices.Options(snapshots)).RunAsync(first.Check.CheckId, CancellationToken.None);
        Assert.Null(await checks.CurrentAsync(email, CancellationToken.None));
        Assert.Equal(CreateOutcome.Created, (await checks.CreateAsync(email, request, CancellationToken.None)).Outcome);
    }

    [Fact]
    public async Task AMissingDateOfBirthIsRefused()
    {
        await using var db = fixture.CreateContext();
        var result = await TestServices.Checks(db).CreateAsync(Email(), new NewCheck("Petr Pavel", null, null, null, null, null, null), CancellationToken.None);

        Assert.Equal(CreateOutcome.Invalid, result.Outcome);
        Assert.Contains("dateOfBirth", result.Message);
    }

    [Fact]
    public async Task AnotherUsersCheckIsNotFound()
    {
        var owner = Email();
        var check = await RunCheckAsync(owner, new NewCheck("Petr Pavel", new DateOnly(1961, 11, 1), null, null, null, null, null));

        await using var db = fixture.CreateContext();
        Assert.Null(await TestServices.Checks(db).GetAsync(Email(), check.CheckId, CancellationToken.None));
        Assert.NotNull(await TestServices.Checks(db).GetAsync(owner, check.CheckId, CancellationToken.None));
        Assert.Single(await TestServices.Checks(db).ListAsync(owner, 20, CancellationToken.None));
    }

    [Fact]
    public async Task LeftoverRunningChecksAreInterruptedOnStart()
    {
        var email = Email();
        await using var db = fixture.CreateContext();
        var checks = TestServices.Checks(db);
        var created = await checks.CreateAsync(email, new NewCheck("Petr Pavel", new DateOnly(1961, 11, 1), null, null, null, null, null), CancellationToken.None);

        var interrupted = await checks.InterruptRunningAsync("restart", CancellationToken.None);
        Assert.True(interrupted >= 1);

        var view = await checks.GetAsync(email, created.Check!.CheckId, CancellationToken.None);
        Assert.Equal(CheckStatus.Failed, view!.Status);
        Assert.Equal("restart", view.Error);
        Assert.All(view.Stages, s => Assert.Equal(StageStatus.Failed, s.Status));
    }
}
