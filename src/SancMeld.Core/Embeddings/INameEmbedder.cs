namespace SancMeld.Core.Embeddings;

/// <summary>
/// Turns names into unit vectors under one model.
///
/// The seam between the matcher and whatever runs the model. Ollama over HTTP today;
/// ONNX Runtime in-process is the intended successor, because a candidate generator that
/// depends on a second process being up is one that is sometimes off. Both must produce
/// the same vectors for the same model file — that is what <see cref="ModelName"/> and the
/// digest on embedding_model are for.
/// </summary>
public interface INameEmbedder
{
    /// <summary>What the provider calls the model, e.g. "bge-m3". Checked against embedding_model.model_name.</summary>
    string ModelName { get; }

    /// <summary>
    /// One unit-length vector per text, in order. Implementations normalise; callers take
    /// a dot product and call it cosine.
    /// </summary>
    Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
