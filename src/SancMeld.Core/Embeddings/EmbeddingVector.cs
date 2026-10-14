using System.Buffers.Binary;
using System.Numerics.Tensors;

namespace SancMeld.Core.Embeddings;

/// <summary>
/// The stored form of a vector: float32, little-endian, nothing else.
///
/// bytea rather than pgvector, deliberately. At tens of thousands of names an exact search
/// over every vector in memory takes single-digit milliseconds, which is faster than a
/// round trip to an index and has no approximation to account for; and the hosts this
/// runs on share a PostgreSQL that cannot have an extension added for one application.
/// The table is the source of truth, the process holds a copy.
/// </summary>
public static class EmbeddingVector
{
    public static byte[] ToBytes(ReadOnlySpan<float> vector)
    {
        var bytes = new byte[vector.Length * sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            BinaryPrimitives.WriteSingleLittleEndian(bytes.AsSpan(i * sizeof(float)), vector[i]);
        }

        return bytes;
    }

    public static float[] FromBytes(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length % sizeof(float) != 0)
        {
            throw new ArgumentException($"{bytes.Length} bytes is not a whole number of floats.", nameof(bytes));
        }

        var vector = new float[bytes.Length / sizeof(float)];

        for (var i = 0; i < vector.Length; i++)
        {
            vector[i] = BinaryPrimitives.ReadSingleLittleEndian(bytes.Slice(i * sizeof(float)));
        }

        return vector;
    }

    /// <summary>In place. A zero vector stays zero rather than becoming NaN.</summary>
    public static void Normalize(Span<float> vector)
    {
        var norm = TensorPrimitives.Norm(vector);

        if (norm > 0)
        {
            TensorPrimitives.Divide(vector, norm, vector);
        }
    }

    /// <summary>Cosine similarity, given both sides are unit length.</summary>
    public static float Dot(ReadOnlySpan<float> a, ReadOnlySpan<float> b) => TensorPrimitives.Dot(a, b);
}
