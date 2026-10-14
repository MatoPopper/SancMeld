namespace SancMeld.Experiments.NameEmbeddings;

/// <summary>
/// Where the confirmed listing landed in each ranking, for one declared link. Ranks are
/// 1-based; null means the ranking did not return it at all (trigram) or it fell outside
/// the fused list (fusion). The embedding ranks the entire list, so its rank is never null.
/// </summary>
public sealed record LinkResult(
    string FromSource,
    string ToSource,
    Guid FromDesignationId,
    Guid ToDesignationId,
    string Query,
    int? TrigramRank,
    int EmbeddingRank,
    int? FusedRank,
    string EmbeddingBestName,
    float EmbeddingBestScore)
{
    public string Pair => $"{FromSource}→{ToSource}";
}

public static class Fusion
{
    /// <summary>
    /// Reciprocal rank fusion of the two top-k lists, Cormack et al. 2009.
    ///
    /// Chosen over a weighted sum of scores because the two scores are not on one scale —
    /// trigram similarity and cosine distance mean different things — and over a plain
    /// union because a union has no order, and the question a screening officer asks is
    /// "what should I look at first". The constant 60 is the paper's; it keeps a single
    /// top rank from dominating.
    /// </summary>
    public static IReadOnlyList<Guid> ReciprocalRank(IReadOnlyList<Guid> first, IReadOnlyList<Guid> second)
    {
        const double k = 60;
        var scores = new Dictionary<Guid, double>();

        foreach (var list in new[] { first, second })
        {
            for (var rank = 0; rank < list.Count; rank++)
            {
                scores[list[rank]] = scores.GetValueOrDefault(list[rank]) + 1 / (k + rank + 1);
            }
        }

        return scores.OrderByDescending(kv => kv.Value).Select(kv => kv.Key).ToList();
    }
}

public static class Metrics
{
    public static int? RankOf(IReadOnlyList<Guid> ranking, Guid target)
    {
        for (var i = 0; i < ranking.Count; i++)
        {
            if (ranking[i] == target)
            {
                return i + 1;
            }
        }

        return null;
    }

    /// <summary>Share of links whose confirmed listing sits within the first k.</summary>
    public static double RecallAt(IEnumerable<int?> ranks, int k)
    {
        var total = 0;
        var hit = 0;

        foreach (var rank in ranks)
        {
            total++;
            if (rank is { } r && r <= k)
            {
                hit++;
            }
        }

        return total == 0 ? 0 : (double)hit / total;
    }

    /// <summary>
    /// Mean reciprocal rank, capped at k: a listing the ranking returns at position 200 is
    /// one no screening would show, and scoring it 1/200 would credit a reach that does
    /// not exist.
    /// </summary>
    public static double MeanReciprocalRank(IEnumerable<int?> ranks, int k)
    {
        var total = 0;
        var sum = 0.0;

        foreach (var rank in ranks)
        {
            total++;
            if (rank is { } r && r <= k)
            {
                sum += 1.0 / r;
            }
        }

        return total == 0 ? 0 : sum / total;
    }
}
