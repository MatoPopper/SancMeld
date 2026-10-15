using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;

namespace SancMeld.Core.Embeddings;

/// <summary>
/// Every current name's vector, in memory, searched by brute force.
///
/// A singleton holding an immutable snapshot that is swapped whole: a search never sees a
/// half-loaded index, and a reload never blocks a search on the old one. The snapshot is
/// dropped — not rebuilt — when the table changes; the next search rebuilds it, so a host
/// that never screens never pays for it.
///
/// Size: 41 304 names × 1 024 floats is 170 MB. Fine for this product's lists; the day it
/// is not, this class is what pgvector replaces, and nothing above it changes.
/// </summary>
public sealed class SemanticNameIndex(
    IServiceScopeFactory scopes,
    IOptions<SancMeldOptions> options,
    ILogger<SemanticNameIndex> logger)
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private volatile SemanticSnapshot? _snapshot;

    /// <summary>Forget the current copy. Called after vectors were written.</summary>
    public void Invalidate() => _snapshot = null;

    public async ValueTask<SemanticSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (_snapshot is { } loaded)
        {
            return loaded;
        }

        await _loading.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            if (_snapshot is { } loadedMeanwhile)
            {
                return loadedMeanwhile;
            }

            var snapshot = await LoadAsync(ct).ConfigureAwait(false);
            _snapshot = snapshot;
            return snapshot;
        }
        finally
        {
            _loading.Release();
        }
    }

    private async Task<SemanticSnapshot> LoadAsync(CancellationToken ct)
    {
        var started = TimeProvider.System.GetTimestamp();
        var settings = options.Value;
        var includeWeak = settings.Matching.ScoreWeakAliases;

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();

        var model = await db.EmbeddingModels
            .Where(m => m.IsActive && m.Code == settings.Embeddings.ModelCode)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (model is null)
        {
            return SemanticSnapshot.Empty;
        }

        // The same filters the trigram search applies — current revisions, weak aliases only
        // when they are scored — so a listing the embedding reaches is one the search could
        // have shown.
        var rows = await db.NameEmbeddings
            .AsNoTracking()
            .Where(e => e.ModelId == model.Id)
            .Where(e => e.Name!.Designation!.ValidTo == null)
            .Where(e => e.Name!.Designation!.Status != DesignationStatus.Delisted)
            .Where(e => includeWeak || !e.Name!.IsWeak)
            .Select(e => new { e.NameId, e.Name!.DesignationId, e.Name.NameOriginal, e.Vector })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var snapshot = new SemanticSnapshot(
            model.Id,
            [.. rows.Select(r => r.DesignationId)],
            [.. rows.Select(r => r.NameId)],
            [.. rows.Select(r => r.NameOriginal)],
            [.. rows.Select(r => EmbeddingVector.FromBytes(r.Vector))]);

        logger.LogInformation(
            "Semantic index loaded: {Count} names under {Model} in {Elapsed} ms.",
            rows.Count, model.Code, TimeProvider.System.GetElapsedTime(started).TotalMilliseconds);

        return snapshot;
    }
}

/// <summary>One listing, by its best-scoring spelling under the model.</summary>
public sealed record SemanticHit(Guid DesignationId, long NameId, string Name, float Score);

/// <summary>
/// The whole ranking of one query against one list state: the ordered top and a lookup
/// for everything else. The lookup is what lets every candidate the trigram found carry
/// a semantic score too, which is the comparison the shadow period is for.
/// </summary>
public sealed class SemanticRanking(IReadOnlyList<SemanticHit> ranked)
{
    private readonly Dictionary<Guid, SemanticHit> _byDesignation = ranked.ToDictionary(h => h.DesignationId);

    /// <summary>Best spelling per listing, highest score first.</summary>
    public IReadOnlyList<SemanticHit> Ranked { get; } = ranked;

    public SemanticHit? Of(Guid designationId) => _byDesignation.GetValueOrDefault(designationId);
}

/// <summary>Immutable; a reload produces a new one.</summary>
public sealed class SemanticSnapshot(Guid modelId, Guid[] designationIds, long[] nameIds, string[] names, float[][] vectors)
{
    public static readonly SemanticSnapshot Empty = new(Guid.Empty, [], [], [], []);

    public Guid ModelId { get; } = modelId;
    public int Count => vectors.Length;

    /// <summary>
    /// Every listing scored by its best spelling. Brute force over all vectors — exact, and
    /// at this size a few milliseconds with SIMD.
    /// </summary>
    public SemanticRanking Rank(ReadOnlySpan<float> query)
    {
        var best = new Dictionary<Guid, (float Score, int Index)>();

        for (var i = 0; i < vectors.Length; i++)
        {
            var score = EmbeddingVector.Dot(query, vectors[i]);

            if (!best.TryGetValue(designationIds[i], out var current) || score > current.Score)
            {
                best[designationIds[i]] = (score, i);
            }
        }

        var ranked = best
            .Select(kv => new SemanticHit(kv.Key, nameIds[kv.Value.Index], names[kv.Value.Index], kv.Value.Score))
            .OrderByDescending(h => h.Score)
            .ThenBy(h => h.DesignationId)
            .ToList();

        return new SemanticRanking(ranked);
    }
}
