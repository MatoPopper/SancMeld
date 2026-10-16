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
/// half-loaded index, and one reload serves every search that arrives while it runs.
///
/// The snapshot follows the table, not the process. The writer that fills the table is as
/// often another process as this one — `import` and `embeddings backfill` run through
/// `docker exec` on the deployed host — so an in-process signal cannot be the only trigger.
/// Each search compares the snapshot's fingerprint (the newest vector under the model and
/// the last successful import) with the database; the fingerprint is two index-backed
/// MAX() queries and costs under a millisecond. When the table has moved on, the search
/// waits for a reload, the same wait the first search after a restart pays.
///
/// The one thing the comparison must not do is reload on every batch while a 20-minute
/// backfill is still writing: a vector younger than <see cref="EmbeddingsOptions.IndexSettleSeconds"/>
/// means the table is in motion, and the reload waits until it has settled. A run that
/// never finishes leaves a last vector that only gets older, so the wait ends by itself.
///
/// Size: 51 066 names × 1 024 floats is 210 MB. Fine for this product's lists; the day it
/// is not, this class is what pgvector replaces, and nothing above it changes.
/// </summary>
public sealed class SemanticNameIndex(
    IServiceScopeFactory scopes,
    IOptions<SancMeldOptions> options,
    TimeProvider clock,
    ILogger<SemanticNameIndex> logger)
{
    private readonly SemaphoreSlim _loading = new(1, 1);
    private volatile SemanticSnapshot? _snapshot;
    private volatile bool _invalidated;

    /// <summary>
    /// Forget the current copy. Called by a writer in this process after it wrote vectors;
    /// the next search reloads without waiting for the table to settle, because the
    /// caller knows it has finished.
    /// </summary>
    public void Invalidate() => _invalidated = true;

    public async ValueTask<SemanticSnapshot> GetAsync(CancellationToken ct = default)
    {
        if (_snapshot is { } loaded && await IsCurrentAsync(loaded, ct).ConfigureAwait(false))
        {
            return loaded;
        }

        await _loading.WaitAsync(ct).ConfigureAwait(false);

        try
        {
            // Another search may have reloaded while this one waited. Its copy is as fresh
            // as this one would be; a second reload would only repeat the work.
            if (_snapshot is { } loadedMeanwhile && await IsCurrentAsync(loadedMeanwhile, ct).ConfigureAwait(false))
            {
                return loadedMeanwhile;
            }

            var snapshot = await LoadAsync(ct).ConfigureAwait(false);
            _snapshot = snapshot;
            _invalidated = false;
            return snapshot;
        }
        finally
        {
            _loading.Release();
        }
    }

    /// <summary>
    /// Whether a search may use this copy: nothing was invalidated in-process, the table
    /// has the same fingerprint, or it has a newer one but is still being written to.
    /// </summary>
    private async ValueTask<bool> IsCurrentAsync(SemanticSnapshot snapshot, CancellationToken ct)
    {
        if (_invalidated)
        {
            return false;
        }

        if (snapshot.ModelId == Guid.Empty)
        {
            // Loaded without a model row. Nothing to compare against; look again.
            return false;
        }

        await using var scope = scopes.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<SancMeldDbContext>();
        var current = await ReadFingerprintAsync(db, snapshot.ModelId, ct).ConfigureAwait(false);

        if (current == snapshot.Fingerprint)
        {
            return true;
        }

        var settle = TimeSpan.FromSeconds(options.Value.Embeddings.IndexSettleSeconds);
        return current.NewestVector is { } newest && clock.GetUtcNow() - newest < settle;
    }

    private static async Task<SemanticIndexFingerprint> ReadFingerprintAsync(SancMeldDbContext db, Guid modelId, CancellationToken ct)
    {
        // Two MAX() over indexed columns: (model_id, created_at) on the vectors, a seven-
        // rows-a-day table for the runs. A COUNT would walk every row and cost 20 ms at
        // 53 000 vectors, and says nothing MAX(created_at) does not: rows are only added.
        var newestVector = await db.NameEmbeddings
            .Where(e => e.ModelId == modelId)
            .MaxAsync(e => (DateTimeOffset?)e.CreatedAt, ct)
            .ConfigureAwait(false);

        // A successful import closes revisions and delists before it embeds, and the
        // embedding may never come when the model is down; the run itself is the signal
        // for everything a vector does not announce.
        var lastImport = await db.ImportRuns
            .Where(r => r.Status == "SUCCESS")
            .MaxAsync(r => r.FinishedAt, ct)
            .ConfigureAwait(false);

        return new SemanticIndexFingerprint(newestVector, lastImport);
    }

    private async Task<SemanticSnapshot> LoadAsync(CancellationToken ct)
    {
        var started = clock.GetTimestamp();
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

        // Read before the rows, not after: a vector written during the load is then newer
        // than the fingerprint and the next search reloads once more, instead of a
        // snapshot that claims a state it only partly holds.
        var fingerprint = await ReadFingerprintAsync(db, model.Id, ct).ConfigureAwait(false);

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
            fingerprint,
            [.. rows.Select(r => r.DesignationId)],
            [.. rows.Select(r => r.NameId)],
            [.. rows.Select(r => r.NameOriginal)],
            [.. rows.Select(r => EmbeddingVector.FromBytes(r.Vector))]);

        logger.LogInformation(
            "Semantic index loaded: {Count} names under {Model} in {Elapsed} ms (vectors as of {NewestVector:O}, last import {LastImport:O}).",
            rows.Count, model.Code, clock.GetElapsedTime(started).TotalMilliseconds,
            fingerprint.NewestVector, fingerprint.LastImport);

        return snapshot;
    }
}

/// <summary>
/// The table state a snapshot was loaded from: when its newest vector was written and when
/// the last successful import finished. Equal fingerprints mean nothing the index holds
/// has changed; rows are added, never edited, so the two timestamps are enough.
/// </summary>
public readonly record struct SemanticIndexFingerprint(DateTimeOffset? NewestVector, DateTimeOffset? LastImport);

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
public sealed class SemanticSnapshot(
    Guid modelId,
    SemanticIndexFingerprint fingerprint,
    Guid[] designationIds,
    long[] nameIds,
    string[] names,
    float[][] vectors)
{
    public static readonly SemanticSnapshot Empty = new(Guid.Empty, default, [], [], [], []);

    public Guid ModelId { get; } = modelId;

    /// <summary>The table state this copy reflects.</summary>
    public SemanticIndexFingerprint Fingerprint { get; } = fingerprint;

    public int Count => vectors.Length;

    /// <summary>Whether a spelling is in this copy. For tests and diagnostics; a search ranks.</summary>
    public bool Contains(long nameId) => Array.IndexOf(nameIds, nameId) >= 0;

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
