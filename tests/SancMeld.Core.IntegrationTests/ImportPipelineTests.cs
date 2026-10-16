using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Ingest;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The pipeline's whole job is deciding what changed since yesterday, and every failure
/// mode is a wrong decision about a person's listing. Each test below is one of those
/// decisions.
///
/// Isolation is by source: every test gets its own sanctions_source row, and the schema
/// keys designations to it, so the tests share a database without sharing state.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ImportPipelineTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "TEST_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-tests", Guid.NewGuid().ToString("N"));

    private Guid _sourceId;

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();

        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");

        var source = new SanctionsSource
        {
            Id = Guid.CreateVersion7(),
            JurisdictionId = jurisdiction.Id,
            Code = _sourceCode,
            Name = _sourceCode,
            Publisher = "Test",
            LegalEffect = LegalEffect.Binding,
            Format = "JSON",
            LandingPageUrl = "https://example.invalid",
            MinExpectedRecords = 1,
        };

        db.Sources.Add(source);
        await db.SaveChangesAsync();
        _sourceId = source.Id;
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_snapshotRoot))
        {
            Directory.Delete(_snapshotRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task FirstImportPublishesEveryRecord()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"), Subject("B", "Maria Gonzalez"));

        var outcome = await RunAsync(connector);

        Assert.Equal(ImportStatus.Success, outcome.Status);
        Assert.Equal(2, outcome.Inserted);
        Assert.Equal(0, outcome.Updated);

        await using var db = fixture.CreateContext();
        Assert.Equal(2, await OpenRevisions(db).CountAsync());
        Assert.Equal(2, await db.Names.CountAsync(n => OpenRevisions(db).Any(d => d.Id == n.DesignationId)));
    }

    [Fact]
    public async Task RepeatedReleaseIsNotDownloadedTwice()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"));
        await RunAsync(connector);

        var second = await RunAsync(connector);

        Assert.Equal(ImportStatus.SkippedUnchanged, second.Status);

        // The point of the version marker: the 25 MB never crossed the wire.
        Assert.Equal(1, connector.DownloadCount);
    }

    [Fact]
    public async Task ChangedRecordOpensANewRevisionAndClosesTheOld()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"));
        await RunAsync(connector);

        connector.Records = [Subject("A", "Ivan Petrov", remarks: "Now also listed under regulation 2026/1")];
        var outcome = await RunAsync(connector);

        Assert.Equal(1, outcome.Updated);
        Assert.Equal(0, outcome.Inserted);

        await using var db = fixture.CreateContext();
        var revisions = await db.Designations
            .Where(d => d.SourceId == _sourceId && d.SourceRecordKey == "A")
            .OrderBy(d => d.ValidFrom)
            .ToListAsync();

        // Both are kept: "was this person listed last March, and what did it say" is the
        // question the whole versioning design exists to answer.
        Assert.Equal(2, revisions.Count);
        Assert.NotNull(revisions[0].ValidTo);
        Assert.Null(revisions[1].ValidTo);
        Assert.Equal("Now also listed under regulation 2026/1", revisions[1].Remarks);
    }

    [Fact]
    public async Task UnchangedRecordKeepsItsRevisionAndIsMarkedAsSeen()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"), Subject("B", "Maria Gonzalez"));
        await RunAsync(connector);

        // B changes, A does not. A must not be rewritten just because the file did.
        connector.Records = [Subject("A", "Ivan Petrov"), Subject("B", "Maria Gonzalez", remarks: "amended")];
        var outcome = await RunAsync(connector);

        Assert.Equal(1, outcome.Unchanged);
        Assert.Equal(1, outcome.Updated);

        await using var db = fixture.CreateContext();
        Assert.Equal(1, await db.Designations.CountAsync(d => d.SourceId == _sourceId && d.SourceRecordKey == "A"));

        var a = await OpenRevisions(db).FirstAsync(d => d.SourceRecordKey == "A");
        Assert.Equal(outcome.ImportRunId, a.LastSeenRunId);
    }

    [Fact]
    public async Task RecordThePublisherStopsSendingIsMarkedNotDeleted()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"), Subject("B", "Maria Gonzalez"));
        await RunAsync(connector);

        connector.Records = [Subject("A", "Ivan Petrov")];
        var outcome = await RunAsync(connector);

        Assert.Equal(1, outcome.MarkedPossiblyMissing);

        await using var db = fixture.CreateContext();
        var b = await OpenRevisions(db).SingleAsync(d => d.SourceRecordKey == "B");

        // Never Delisted: a truncated file, a schema change and an actual delisting look
        // identical from here, and only one of them means the sanction has been lifted.
        Assert.Equal(DesignationStatus.PossiblyMissing, b.Status);
    }

    [Fact]
    public async Task RecordThatComesBackBecomesActiveAgain()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"), Subject("B", "Maria Gonzalez"));
        await RunAsync(connector);

        connector.Records = [Subject("A", "Ivan Petrov")];
        await RunAsync(connector);

        connector.Records = [Subject("A", "Ivan Petrov"), Subject("B", "Maria Gonzalez")];
        var outcome = await RunAsync(connector);

        // The content hash cannot notice this on its own: PossiblyMissing is our
        // assessment, not something the publisher ever wrote.
        Assert.Equal(1, outcome.Reappeared);

        await using var db = fixture.CreateContext();
        var b = await OpenRevisions(db).SingleAsync(d => d.SourceRecordKey == "B");
        Assert.Equal(DesignationStatus.Active, b.Status);
    }

    [Fact]
    public async Task RejectedDatasetPublishesNothingAndLeavesTheListAlone()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"), Subject("B", "Maria Gonzalez"));
        await RunAsync(connector);

        // A truncated download: one record survives, and the connector's guards say no.
        connector.Records = [Subject("A", "Ivan Petrov")];
        connector.RejectWith = "Record count fell below the floor";
        var outcome = await RunAsync(connector);

        Assert.Equal(ImportStatus.RejectedValidation, outcome.Status);

        await using var db = fixture.CreateContext();

        // Both still live, and B was not marked missing: nothing downstream of the guard ran.
        Assert.Equal(2, await OpenRevisions(db).CountAsync());
        Assert.All(await OpenRevisions(db).ToListAsync(), d => Assert.Equal(DesignationStatus.Active, d.Status));

        // Staging is emptied even on the path that publishes nothing.
        Assert.Equal(0, await db.ImportStaging.CountAsync(s => s.ImportRunId == outcome.ImportRunId));
    }

    [Fact]
    public async Task ChildRowsBelongToTheRevisionThatCarriesThem()
    {
        var connector = Connector(Subject("A", "Ivan Petrov", withPassport: true));
        await RunAsync(connector);

        connector.Records = [Subject("A", "Ivan Petrov", remarks: "amended", withPassport: true)];
        await RunAsync(connector);

        await using var db = fixture.CreateContext();
        var ids = await db.Designations
            .Where(d => d.SourceId == _sourceId && d.SourceRecordKey == "A")
            .Select(d => d.Id)
            .ToListAsync();

        // Two revisions, each with its own copy. A shared child row would make the closed
        // revision change whenever the open one did, which defeats keeping history at all.
        Assert.Equal(2, ids.Count);
        Assert.All(ids, id => Assert.Equal(1, db.Identifiers.Count(i => i.DesignationId == id)));
    }

    [Fact]
    public async Task ImportRunRecordsWhatHappened()
    {
        var connector = Connector(Subject("A", "Ivan Petrov"));
        var outcome = await RunAsync(connector);

        await using var db = fixture.CreateContext();
        var run = await db.ImportRuns.SingleAsync(r => r.Id == outcome.ImportRunId);

        Assert.Equal("SUCCESS", run.Status);
        Assert.Equal(1, run.RecordsSeen);
        Assert.Equal(1, run.RecordsInserted);
        Assert.NotNull(run.FinishedAt);

        // The artefact the decision was based on is kept, so it can be replayed later.
        Assert.True(await db.Snapshots.AnyAsync(s => s.ImportRunId == run.Id));
    }

    private IQueryable<Designation> OpenRevisions(SancMeldDbContext db) =>
        db.Designations.Where(d => d.SourceId == _sourceId && d.ValidTo == null);

    private FakeConnector Connector(params ParsedDesignation[] records) =>
        new(_sourceCode) { Records = [.. records] };

    private async Task<ImportOutcome> RunAsync(FakeConnector connector)
    {
        await using var db = fixture.CreateContext();

        var options = Options.Create(new SancMeldOptions
        {
            Ingest = new IngestOptions { SnapshotStoragePath = _snapshotRoot },
        });

        var service = new SanctionsImportService(
            db,
            [connector],
            new FileSystemSnapshotStore(options),
            TestServices.EmbeddingStore(fixture, db, options), [],
            TimeProvider.System,
            NullLogger<SanctionsImportService>.Instance);

        return await service.RunAsync(_sourceCode, CancellationToken.None);
    }

    private static ParsedDesignation Subject(
        string key, string name, string? remarks = null, bool withPassport = false) => new()
        {
            SourceRecordKey = key,
            ExternalId = key,
            SubjectType = SubjectType.Person,
            Remarks = remarks,
            Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
            Identifiers = withPassport
                ?
                [
                    new ParsedIdentifier
                    {
                        Kind = IdentifierType.Passport,
                        ValueOriginal = "AB-123456",
                        ValueNormalized = "AB123456",
                    },
                ]
                : [],
            RawRecordJson = $$"""{"key":"{{key}}"}""",
        };
}
