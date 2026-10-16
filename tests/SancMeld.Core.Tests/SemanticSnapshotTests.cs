using SancMeld.Core.Embeddings;
using Xunit;

namespace SancMeld.Core.Tests;

public sealed class SemanticSnapshotTests
{
    private static readonly Guid A = Guid.Parse("00000000-0000-0000-0000-00000000000a");
    private static readonly Guid B = Guid.Parse("00000000-0000-0000-0000-00000000000b");

    [Fact]
    public void AListingIsRankedByItsBestSpelling()
    {
        // A has a poor spelling and a good one; B has one middling spelling. B must not
        // outrank A just because A also carries a bad alias.
        var snapshot = new SemanticSnapshot(
            Guid.NewGuid(),
            default,
            [A, A, B],
            [1, 2, 3],
            ["a poor", "a good", "b"],
            [[0f, 1f], [1f, 0f], [0.7f, 0.7f]]);

        var ranking = snapshot.Rank([1f, 0f]);

        Assert.Equal([A, B], ranking.Ranked.Select(h => h.DesignationId));
        Assert.Equal("a good", ranking.Ranked[0].Name);
        Assert.Equal(2, ranking.Ranked[0].NameId);
        Assert.Equal(1f, ranking.Ranked[0].Score, precision: 5);
    }

    [Fact]
    public void EveryListingIsReachableByLookup()
    {
        var snapshot = new SemanticSnapshot(Guid.NewGuid(), default, [A, B], [1, 2], ["a", "b"], [[1f, 0f], [0f, 1f]]);

        var ranking = snapshot.Rank([1f, 0f]);

        Assert.Equal(0f, ranking.Of(B)!.Score, precision: 5);
        Assert.Null(ranking.Of(Guid.NewGuid()));
    }

    [Fact]
    public void EmptySnapshotRanksNothing()
    {
        Assert.Empty(SemanticSnapshot.Empty.Rank([1f, 0f]).Ranked);
        Assert.Equal(0, SemanticSnapshot.Empty.Count);
    }
}
