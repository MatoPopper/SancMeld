using SancMeld.Core.Embeddings;
using Xunit;

namespace SancMeld.Core.Tests;

public sealed class EmbeddingVectorTests
{
    [Fact]
    public void BytesRoundTripExactly()
    {
        float[] vector = [0.25f, -1.5f, 3.0e-8f, float.MaxValue, 0f];

        var back = EmbeddingVector.FromBytes(EmbeddingVector.ToBytes(vector));

        Assert.Equal(vector, back);
    }

    [Fact]
    public void BytesAreLittleEndianFloat32()
    {
        // 1.0f is 0x3F800000; the format is part of the schema, so it is pinned here
        // rather than left to whatever the platform does.
        var bytes = EmbeddingVector.ToBytes([1.0f]);

        Assert.Equal(new byte[] { 0x00, 0x00, 0x80, 0x3F }, bytes);
    }

    [Fact]
    public void APartialFloatIsRefused()
    {
        Assert.Throws<ArgumentException>(() => EmbeddingVector.FromBytes(new byte[5]));
    }

    [Fact]
    public void NormalizeGivesUnitLength()
    {
        float[] vector = [3f, 4f];

        EmbeddingVector.Normalize(vector);

        Assert.Equal(0.6f, vector[0], precision: 6);
        Assert.Equal(0.8f, vector[1], precision: 6);
    }

    [Fact]
    public void ZeroVectorStaysZero()
    {
        float[] vector = [0f, 0f, 0f];

        EmbeddingVector.Normalize(vector);

        Assert.All(vector, v => Assert.Equal(0f, v));
    }
}
