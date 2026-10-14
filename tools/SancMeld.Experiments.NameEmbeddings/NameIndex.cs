using System.Numerics.Tensors;

namespace SancMeld.Experiments.NameEmbeddings;

/// <summary>One recorded spelling of one listing, on the list a link points into.</summary>
public sealed record NameRow(string SourceCode, Guid DesignationId, string Name);

/// <summary>A listing ranked by its best-scoring spelling, which is what the trigram search reports too.</summary>
public sealed record RankedSubject(Guid DesignationId, string Name, float Score);

/// <summary>
/// Every current name on the target lists as a unit vector, searched by brute force.
///
/// Brute force on purpose. The question is whether the model reaches the pairs at all,
/// and an approximate index would put its own misses into that number. 20 000 names fit in
/// 80 MB; exact search over them takes milliseconds with SIMD.
/// </summary>
public sealed class NameIndex
{
    private sealed record Block(Guid[] Designations, string[] Names, float[][] Vectors);

    private readonly Dictionary<string, Block> _bySource;
    private readonly OllamaEmbedder _embedder;
    private readonly EmbeddingCache _cache;

    private NameIndex(Dictionary<string, Block> bySource, OllamaEmbedder embedder, EmbeddingCache cache)
    {
        _bySource = bySource;
        _embedder = embedder;
        _cache = cache;
    }

    public int Dimensions => _bySource.Values.First().Vectors[0].Length;

    public static async Task<NameIndex> BuildAsync(
        IReadOnlyList<NameRow> rows,
        string namePrefix,
        OllamaEmbedder embedder,
        EmbeddingCache cache,
        IProgress<(int Done, int Total)> progress,
        CancellationToken ct = default)
    {
        var texts = rows.Select(r => namePrefix + r.Name).ToArray();
        var vectors = await EmbedThroughCacheAsync(texts, embedder, cache, progress, ct);

        var bySource = rows
            .Select((row, i) => (row, i))
            .GroupBy(x => x.row.SourceCode)
            .ToDictionary(
                g => g.Key,
                g => new Block(
                    [.. g.Select(x => x.row.DesignationId)],
                    [.. g.Select(x => x.row.Name)],
                    [.. g.Select(x => vectors[x.i])]));

        return new NameIndex(bySource, embedder, cache);
    }

    public async Task<float[][]> EmbedQueriesAsync(
        IReadOnlyList<string> queries,
        IProgress<(int Done, int Total)> progress,
        CancellationToken ct = default) =>
        await EmbedThroughCacheAsync(queries, _embedder, _cache, progress, ct);

    /// <summary>Every listing on one list, best spelling first. The full order, not a top-k.</summary>
    public IReadOnlyList<RankedSubject> Rank(string sourceCode, float[] query)
    {
        var block = _bySource[sourceCode];
        var best = new Dictionary<Guid, (float Score, int Index)>();

        for (var i = 0; i < block.Vectors.Length; i++)
        {
            var score = TensorPrimitives.Dot(query, block.Vectors[i]);

            if (!best.TryGetValue(block.Designations[i], out var current) || score > current.Score)
            {
                best[block.Designations[i]] = (score, i);
            }
        }

        return best
            .Select(kv => new RankedSubject(kv.Key, block.Names[kv.Value.Index], kv.Value.Score))
            .OrderByDescending(r => r.Score)
            .ToList();
    }

    private static async Task<float[][]> EmbedThroughCacheAsync(
        IReadOnlyList<string> texts,
        OllamaEmbedder embedder,
        EmbeddingCache cache,
        IProgress<(int Done, int Total)> progress,
        CancellationToken ct)
    {
        // Small batches: Ollama holds the whole batch's activations, and a CPU box with
        // other work on it is better served by many short requests than one that pins
        // every core for a minute.
        const int batchSize = 32;
        const int saveEvery = 20;

        var result = new float[texts.Count][];
        var missing = new List<int>();

        for (var i = 0; i < texts.Count; i++)
        {
            if (cache.TryGet(texts[i], out var cached))
            {
                result[i] = cached;
            }
            else
            {
                missing.Add(i);
            }
        }

        var done = texts.Count - missing.Count;
        progress.Report((done, texts.Count));

        var batches = 0;
        foreach (var batch in missing.Chunk(batchSize))
        {
            var vectors = await embedder.EmbedAsync([.. batch.Select(i => texts[i])], ct);

            for (var b = 0; b < batch.Length; b++)
            {
                result[batch[b]] = vectors[b];
                cache.Add(texts[batch[b]], vectors[b]);
            }

            done += batch.Length;
            progress.Report((done, texts.Count));

            if (++batches % saveEvery == 0)
            {
                cache.Save();
            }
        }

        cache.Save();
        return result;
    }
}
