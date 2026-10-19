using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Embeddings;
using SancMeld.Core.Ingest;
using SancMeld.Core.Relations;
using SancMeld.Core.Search;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The service graph by hand, the way the host wires it, so a test that wants an import
/// or a screening does not restate five constructors.
/// </summary>
internal static class TestServices
{
    /// <summary>
    /// The embedding_model row the fake embedder answers for. Dimensions and model name
    /// have to agree with the row or the store refuses to write, which is the point of
    /// the check and the reason the seeded bge-m3 row cannot be used here.
    /// </summary>
    public static readonly Guid FakeModelId = new("0195a1f0-0000-7000-8000-00000000f301");

    public const string FakeModelCode = "FAKE";

    public static async Task EnsureFakeModelAsync(SancMeldDbContext db)
    {
        if (await db.EmbeddingModels.AnyAsync(m => m.Id == FakeModelId))
        {
            return;
        }

        db.EmbeddingModels.Add(new EmbeddingModel
        {
            Id = FakeModelId,
            Code = FakeModelCode,
            Version = "1",
            Provider = "TEST",
            ModelName = FakeNameEmbedder.Model,
            Dimensions = FakeNameEmbedder.Dimensions,
            IsActive = true,
            CreatedAt = WellKnownIds.SeededAt,
        });

        await db.SaveChangesAsync();
    }

    public static IOptions<SancMeldOptions> Options(string snapshotRoot, EmbeddingsMode mode = EmbeddingsMode.Off, int indexSettleSeconds = 0) =>
        Microsoft.Extensions.Options.Options.Create(new SancMeldOptions
        {
            Ingest = new IngestOptions { SnapshotStoragePath = snapshotRoot },
            // No settling by default: a test writes and then searches at once, and the
            // search must see the write. The tests of the settling itself set it.
            Embeddings = new EmbeddingsOptions { Mode = mode, ModelCode = FakeModelCode, IndexSettleSeconds = indexSettleSeconds },
        });

    public static SemanticNameIndex Index(DatabaseFixture fixture, IOptions<SancMeldOptions> options, TimeProvider? clock = null) =>
        new(fixture.ScopeFactory, options, clock ?? TimeProvider.System, NullLogger<SemanticNameIndex>.Instance);

    public static NameEmbeddingStore EmbeddingStore(
        DatabaseFixture fixture,
        SancMeldDbContext db,
        IOptions<SancMeldOptions> options,
        INameEmbedder? embedder = null,
        SemanticNameIndex? index = null) =>
        new(db, embedder ?? new FakeNameEmbedder(), index ?? Index(fixture, options), options,
            TimeProvider.System, NullLogger<NameEmbeddingStore>.Instance);

    public static SanctionsImportService Importer(
        DatabaseFixture fixture,
        SancMeldDbContext db,
        ISanctionsConnector connector,
        IOptions<SancMeldOptions> options,
        INameEmbedder? embedder = null,
        SemanticNameIndex? index = null,
        IRelatedPartyEnricher? enricher = null) =>
        new(db, [connector], new FileSystemSnapshotStore(options),
            EmbeddingStore(fixture, db, options, embedder, index),
            enricher is null ? [] : [enricher],
            TimeProvider.System,
            NullLogger<SanctionsImportService>.Instance);

    public static SemanticCandidateSource Semantic(
        DatabaseFixture fixture,
        IOptions<SancMeldOptions> options,
        INameEmbedder? embedder = null,
        SemanticNameIndex? index = null) =>
        new(embedder ?? new FakeNameEmbedder(), index ?? Index(fixture, options), options,
            NullLogger<SemanticCandidateSource>.Instance);

    /// <summary>Sees the additional information unless a test says otherwise: most tests are about what is stored, not who sees it.</summary>
    public static RelatedPartyQueryService RelatedParties(SancMeldDbContext db, DesignationQueryService queries, bool additionalInfo = true) =>
        new(db, queries, Options(Path.GetTempPath()),
            additionalInfo ? new SancMeld.Core.Access.FullAdditionalInfoAccess() : new SancMeld.Core.Access.NoAdditionalInfoAccess());

    public static RelatedPartyStore RelatedPartyStore(SancMeldDbContext db, TimeProvider? clock = null) =>
        new(db, clock ?? TimeProvider.System, NullLogger<RelatedPartyStore>.Instance);

    public static ScreeningService Screening(
        DatabaseFixture fixture,
        SancMeldDbContext db,
        IOptions<SancMeldOptions> options,
        INameEmbedder? embedder = null,
        SemanticNameIndex? index = null) =>
        new(db, new DesignationQueryService(db, options), new RulesScoringStrategy(),
            Semantic(fixture, options, embedder, index), options, TimeProvider.System);
}
