using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Embeddings;
using SancMeld.Core.Ingest;
using SancMeld.Core.Search;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The embedding beside the trigram, in shadow: vectors are written at import, a listing
/// the trigram cannot reach is recorded as a candidate and not shown, and a model that
/// is off or down leaves the screening exactly as it was.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class NameEmbeddingTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "EMB_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-emb", Guid.NewGuid().ToString("N"));
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
    public async Task ImportEmbedsEveryCurrentNameInShadowMode()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);

        await ImportAsync(db, options, new FakeNameEmbedder(),
            Subject("a", Named("pavel burzynski"), alias: Named("pasha burzynski")),
            Subject("b", Named("ivan petrov")));

        var names = await db.Names.Where(n => n.Designation!.Source!.Code == _sourceCode).Select(n => n.Id).ToListAsync();
        var embedded = await db.NameEmbeddings
            .CountAsync(e => names.Contains(e.NameId) && e.ModelId == TestServices.FakeModelId);

        Assert.Equal(3, names.Count);
        Assert.Equal(3, embedded);
    }

    [Fact]
    public async Task ImportInOffModeStoresNothingAndCallsNoModel()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Off);
        var embedder = new FakeNameEmbedder();

        await ImportAsync(db, options, embedder, Subject("a", Named("pavel burzynski")));

        var names = await db.Names.Where(n => n.Designation!.Source!.Code == _sourceCode).Select(n => n.Id).ToListAsync();

        Assert.Equal(0, embedder.Calls);
        Assert.Equal(0, await db.NameEmbeddings.CountAsync(e => names.Contains(e.NameId)));
    }

    [Fact]
    public async Task AModelOutageDoesNotFailTheImport()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);
        var embedder = new FakeNameEmbedder { Outage = new EmbeddingUnavailableException("down") };

        var outcome = await ImportAsync(db, options, embedder, Subject("a", Named("pavel burzynski")));

        Assert.Equal(ImportStatus.Success, outcome.Status);

        // And the backfill picks the names up once the model is back.
        embedder.Outage = null;
        var written = await TestServices.EmbeddingStore(fixture, db, options, embedder).EmbedMissingAsync(_sourceCode);

        Assert.Equal(1, written);
    }

    [Fact]
    public async Task BackfillIsIdempotent()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);
        var embedder = new FakeNameEmbedder();

        await ImportAsync(db, options, embedder, Subject("a", Named("pavel burzynski")));
        var callsAfterImport = embedder.Calls;

        var written = await TestServices.EmbeddingStore(fixture, db, options, embedder).EmbedMissingAsync(_sourceCode);

        Assert.Equal(0, written);
        Assert.Equal(callsAfterImport, embedder.Calls);
    }

    [Fact]
    public async Task ShadowRecordsWhatOnlyTheEmbeddingReachedAndShowsNothingNew()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);

        // The whole case for the embedding: the same subject in another script. The fake
        // model "knows" the two spellings are one; the trigram has no character in common.
        var latin = Named("michail grib");
        // No tag in the query: the trigram's word branch would match the tag itself and
        // the point is a query it cannot reach.
        var cyrillic = "Міхаіл Грыб";
        var embedder = new FakeNameEmbedder().SameAs(cyrillic, latin);

        await ImportAsync(db, options, embedder, Subject("a", latin), Subject("b", Named("ivan petrov")));
        var target = await DesignationIdAsync(db, "a");

        var result = await TestServices.Screening(fixture, db, options, embedder)
            .ScreenAsync(new ScreeningQuery { Name = cyrillic });

        // Nothing shown: a shadow is a shadow.
        Assert.DoesNotContain(result.Hits, h => h.Designation.Id == target);

        // But recorded, as the embedding's find, below everything that was shown.
        var recorded = await db.ScreeningCandidates
            .Where(c => c.RequestId == result.RequestId)
            .ToListAsync();

        var shadow = Assert.Single(recorded, c => c.DesignationId == target);
        Assert.Equal(CandidateOrigin.Semantic, shadow.RetrievedBy);
        Assert.False(shadow.AboveThreshold);
        Assert.True(shadow.IsExploration);
        Assert.NotNull(shadow.SemanticScore);
        Assert.True(shadow.SemanticScore > 0.99m, $"semantic score was {shadow.SemanticScore}");

        // The embedding contributes its whole top-k as shadows, best first; the subject
        // it was told about is the best, and every shadow ranks after everything shown.
        Assert.Equal(result.Hits.Count + 1, shadow.Rank);
        Assert.All(recorded.Where(c => c.Rank > result.Hits.Count), c => Assert.Equal(CandidateOrigin.Semantic, c.RetrievedBy));
    }

    [Fact]
    public async Task ATrigramHitCarriesItsSemanticScoreAndOrigin()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);
        var embedder = new FakeNameEmbedder();
        var name = Named("pavel burzynski");

        await ImportAsync(db, options, embedder, Subject("a", name));
        var target = await DesignationIdAsync(db, "a");

        var result = await TestServices.Screening(fixture, db, options, embedder)
            .ScreenAsync(new ScreeningQuery { Name = name });

        var hit = Assert.Single(result.Hits, h => h.Designation.Id == target);
        Assert.Equal(CandidateOrigin.Both, hit.RetrievedBy);
        Assert.NotNull(hit.SemanticScore);
        Assert.True(hit.SemanticScore > 0.99);

        var recorded = await db.ScreeningCandidates.SingleAsync(c => c.RequestId == result.RequestId && c.DesignationId == target);
        Assert.Equal(CandidateOrigin.Both, recorded.RetrievedBy);
        Assert.True(recorded.SemanticScore > 0.99m);
    }

    [Fact]
    public async Task AModelOutageLeavesTheScreeningAsItWas()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Shadow);
        var embedder = new FakeNameEmbedder();
        var name = Named("pavel burzynski");

        await ImportAsync(db, options, embedder, Subject("a", name));
        var target = await DesignationIdAsync(db, "a");

        embedder.Outage = new EmbeddingUnavailableException("down");
        var result = await TestServices.Screening(fixture, db, options, embedder)
            .ScreenAsync(new ScreeningQuery { Name = name });

        var hit = Assert.Single(result.Hits, h => h.Designation.Id == target);
        Assert.Equal(CandidateOrigin.Trigram, hit.RetrievedBy);
        Assert.Null(hit.SemanticScore);
    }

    [Fact]
    public async Task OffModeRecordsNoOriginBeyondTheTrigram()
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot, EmbeddingsMode.Off);
        var embedder = new FakeNameEmbedder();
        var name = Named("pavel burzynski");

        await ImportAsync(db, options, embedder, Subject("a", name));
        var target = await DesignationIdAsync(db, "a");

        var result = await TestServices.Screening(fixture, db, options, embedder)
            .ScreenAsync(new ScreeningQuery { Name = name });

        var hit = Assert.Single(result.Hits, h => h.Designation.Id == target);
        Assert.Equal(CandidateOrigin.Trigram, hit.RetrievedBy);
        Assert.Null(hit.SemanticScore);
        Assert.Equal(0, embedder.Calls);
    }

    private async Task<ImportOutcome> ImportAsync(
        SancMeldDbContext db,
        Microsoft.Extensions.Options.IOptions<SancMeldOptions> options,
        FakeNameEmbedder embedder,
        params ParsedDesignation[] records)
    {
        var outcome = await TestServices
            .Importer(fixture, db, new FakeConnector(_sourceCode) { Records = [.. records] }, options, embedder)
            .RunAsync(_sourceCode, force: true, CancellationToken.None);

        Assert.Equal(ImportStatus.Success, outcome.Status);
        return outcome;
    }

    private async Task<Guid> DesignationIdAsync(SancMeldDbContext db, string key) =>
        await db.Designations
            .Where(d => d.Source!.Code == _sourceCode && d.SourceRecordKey == key && d.ValidTo == null)
            .Select(d => d.Id)
            .SingleAsync();

    private static ParsedDesignation Subject(string key, string name, string? alias = null) => new()
    {
        SourceRecordKey = key,
        ExternalId = key,
        SubjectType = SubjectType.Person,
        Names = alias is null
            ? [new ParsedName { Value = name, Kind = NameType.Primary }]
            : [new ParsedName { Value = name, Kind = NameType.Primary }, new ParsedName { Value = alias, Kind = NameType.Alias }],
        RawRecordJson = $$"""{"key":"{{key}}"}""",
    };
}
