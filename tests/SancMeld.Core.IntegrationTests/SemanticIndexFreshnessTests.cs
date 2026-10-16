using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Embeddings;
using SancMeld.Core.Ingest;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The in-memory index follows the table, whoever wrote it. On the deployed host the
/// writer is `docker exec … import`, a process the API never hears from; these tests
/// stand that process in with a second index instance, which gets the writer's
/// Invalidate() while the index under test gets nothing but the database.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SemanticIndexFreshnessTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "IDX_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-idx", Guid.NewGuid().ToString("N"));
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];

    private string Named(string seed) => $"{_tag}{seed}";

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        await TestServices.EnsureFakeModelAsync(db);

        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");
        db.Sources.Add(new SanctionsSource
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
        });

        await db.SaveChangesAsync();
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
    public async Task AnImportInAnotherProcessIsSeenWithoutAnInvalidate()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);
        var index = TestServices.Index(fixture, options);

        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzynski")));
        var before = await index.GetAsync();

        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzynski")), Subject("b", Named("ivan petrov")));
        var after = await index.GetAsync();

        Assert.NotSame(before, after);
        Assert.True(after.Contains(await NameIdAsync(db, Named("ivan petrov"))));
        Assert.False(before.Contains(await NameIdAsync(db, Named("ivan petrov"))));
    }

    [Fact]
    public async Task ASpellingTheImportSupersededLeavesTheIndex()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);
        var index = TestServices.Index(fixture, options);

        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzynski")));
        var oldName = await NameIdAsync(db, Named("pavel burzynski"));
        var before = await index.GetAsync();
        Assert.True(before.Contains(oldName));

        // The publisher corrected the spelling: a new revision, the old one closed. The
        // old vector stays in the table — a past screening may point at it — but the
        // index must stop searching it.
        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzinski")));
        var after = await index.GetAsync();

        Assert.False(after.Contains(oldName));
        Assert.True(after.Contains(await NameIdAsync(db, Named("pavel burzinski"))));
    }

    [Fact]
    public async Task AnUnchangedTableIsNotReloaded()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);
        var index = TestServices.Index(fixture, options);

        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzynski")));

        var first = await index.GetAsync();
        var second = await index.GetAsync();

        Assert.Same(first, second);
    }

    [Fact]
    public async Task ATableStillBeingWrittenWaitsUntilItSettles()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow, indexSettleSeconds: 10);
        var clock = new FakeClock(DateTimeOffset.UtcNow);
        var index = TestServices.Index(fixture, options, clock);

        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzynski")));
        var before = await index.GetAsync();

        // A backfill is writing: the newest vector is seconds old by the index's clock,
        // which stands still while the writer's runs. The fingerprint has changed, and
        // the search still gets the copy it has.
        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzynski")), Subject("b", Named("ivan petrov")));
        var during = await index.GetAsync();
        Assert.Same(before, during);

        // A minute of silence, and the next search reloads.
        clock.Advance(TimeSpan.FromMinutes(1));
        var after = await index.GetAsync();

        Assert.NotSame(before, after);
        Assert.True(after.Contains(await NameIdAsync(db, Named("ivan petrov"))));
    }

    [Fact]
    public async Task AWriterInThisProcessIsSeenAtOnce()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow, indexSettleSeconds: 3600);
        var index = TestServices.Index(fixture, options);

        await ImportElsewhereAsync(db, options, Subject("a", Named("pavel burzynski")));
        var before = await index.GetAsync();

        // The scheduled import inside the API: it writes through this index's store and
        // invalidates it, so no settling applies — the writer knows it has finished.
        var inProcess = TestServices.Importer(fixture, db,
            new FakeConnector(_sourceCode) { Records = [Subject("a", Named("pavel burzynski")), Subject("b", Named("ivan petrov"))] },
            options, index: index);
        Assert.Equal(ImportStatus.Success, (await inProcess.RunAsync(_sourceCode, force: true, CancellationToken.None)).Status);

        var after = await index.GetAsync();

        Assert.NotSame(before, after);
        Assert.True(after.Contains(await NameIdAsync(db, Named("ivan petrov"))));
    }

    /// <summary>
    /// An import as the deployed host runs it: a process of its own, with an index of its
    /// own that is the only one its Invalidate() reaches.
    /// </summary>
    private async Task ImportElsewhereAsync(SancMeldDbContext db, IOptions<SancMeldOptions> options, params ParsedDesignation[] records)
    {
        var importer = TestServices.Importer(fixture, db,
            new FakeConnector(_sourceCode) { Records = [.. records] },
            options, index: TestServices.Index(fixture, options));

        var outcome = await importer.RunAsync(_sourceCode, force: true, CancellationToken.None);
        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    private async Task<long> NameIdAsync(SancMeldDbContext db, string name) =>
        await db.Names
            .Where(n => n.Designation!.Source!.Code == _sourceCode && n.NameOriginal == name)
            .OrderByDescending(n => n.Id)
            .Select(n => n.Id)
            .FirstAsync();

    private static ParsedDesignation Subject(string key, string name) => new()
    {
        SourceRecordKey = key,
        ExternalId = key,
        SubjectType = SubjectType.Person,
        Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
        RawRecordJson = $$"""{"key":"{{key}}","name":"{{name}}"}""",
    };

    private sealed class FakeClock(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public override DateTimeOffset GetUtcNow() => _now;

        public void Advance(TimeSpan by) => _now += by;
    }
}
