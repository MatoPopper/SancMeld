using SancMeld.Core.Embeddings;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// Deterministic vectors with no model behind them.
///
/// Each distinct text gets a random-looking unit vector seeded from the text, so two
/// unrelated names are near-orthogonal and the same name always embeds the same way.
/// <see cref="SameAs"/> declares two spellings to be one subject — the one thing a real
/// multilingual model does that these tests need, without 600 MB of weights to do it.
/// </summary>
internal sealed class FakeNameEmbedder : INameEmbedder
{
    public const int Dimensions = 16;
    public const string Model = "fake-embedder";

    private readonly Dictionary<string, string> _sameAs = new(StringComparer.Ordinal);

    public string ModelName => Model;

    /// <summary>Thrown by every call when set — a model that is down.</summary>
    public Exception? Outage { get; set; }

    public int Calls { get; private set; }

    public FakeNameEmbedder SameAs(string spelling, string asThough)
    {
        _sameAs[spelling] = asThough;
        return this;
    }

    public Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        Calls++;

        if (Outage is { } outage)
        {
            throw outage;
        }

        return Task.FromResult(texts.Select(t => Vector(_sameAs.GetValueOrDefault(t, t))).ToArray());
    }

    private static float[] Vector(string text)
    {
        // A stable hash: string.GetHashCode is randomised per process.
        var seed = 17;
        foreach (var ch in text)
        {
            seed = unchecked(seed * 31 + ch);
        }

        var random = new Random(seed);
        var vector = new float[Dimensions];
        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = (float)(random.NextDouble() * 2 - 1);
        }

        EmbeddingVector.Normalize(vector);
        return vector;
    }
}
