using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Core.Embeddings;

/// <summary>
/// The embedding's answer to one screening query, or nothing.
///
/// Nothing is a first-class outcome here. The trigram is the matcher this product promises;
/// the embedding adds to it and must never subtract — so when the model is off, not
/// running, serving the wrong thing, or has no vectors to search, the screening proceeds
/// exactly as it would have without this class, and the log says why.
/// </summary>
public sealed class SemanticCandidateSource(
    INameEmbedder embedder,
    SemanticNameIndex index,
    IOptions<SancMeldOptions> options,
    ILogger<SemanticCandidateSource> logger)
{
    public EmbeddingsMode Mode => options.Value.Embeddings.Mode;

    public async Task<SemanticRanking?> RankAsync(string name, CancellationToken ct = default)
    {
        var settings = options.Value.Embeddings;

        if (settings.Mode == EmbeddingsMode.Off || string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        try
        {
            var snapshot = await index.GetAsync(ct).ConfigureAwait(false);

            if (snapshot.Count == 0)
            {
                // Mode is on but the table is empty: a deployment that has not run the
                // backfill yet. Not an error, but worth one line per query until it is.
                logger.LogWarning("Embeddings are {Mode} but no vectors are stored. Run 'embeddings backfill'.", settings.Mode);
                return null;
            }

            var vectors = await embedder.EmbedAsync([settings.QueryPrefix + name], ct).ConfigureAwait(false);
            return snapshot.Rank(vectors[0]);
        }
        catch (Exception ex) when (ex is EmbeddingUnavailableException or HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
        {
            logger.LogWarning(ex, "Semantic candidates skipped for this screening: {Reason}", ex.Message);
            return null;
        }
    }
}
