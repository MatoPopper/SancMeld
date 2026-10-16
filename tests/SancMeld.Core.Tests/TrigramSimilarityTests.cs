using SancMeld.Core.Matching;
using Xunit;

namespace SancMeld.Core.Tests;

public sealed class TrigramSimilarityTests
{
    [Fact]
    public void MatchesTheDocumentedPgTrgmValue()
    {
        // From the pg_trgm documentation: similarity('word', 'two words') = 0.363636.
        Assert.Equal(0.363636, TrigramSimilarity.Similarity("word", "two words"), precision: 6);
    }

    [Fact]
    public void IdenticalStringsScoreOne()
    {
        Assert.Equal(1.0, TrigramSimilarity.Similarity("ramzan kadyrov", "ramzan kadyrov"));
    }

    [Fact]
    public void NothingInCommonScoresZero()
    {
        Assert.Equal(0.0, TrigramSimilarity.Similarity("abc", "xyz"));
        Assert.Equal(0.0, TrigramSimilarity.Similarity("", "xyz"));
    }

    [Fact]
    public void EachWordIsPaddedOnItsOwn()
    {
        // "  wo" and "rd " belong to the word, not to the gap between words, so the
        // trigrams of "two words" are those of "two" plus those of "words".
        var both = TrigramSimilarity.Trigrams("two words");
        var separately = TrigramSimilarity.Trigrams("two").Union(TrigramSimilarity.Trigrams("words")).ToHashSet();

        Assert.Equal(separately, both.ToHashSet());
    }
}

public sealed class CyrillicTransliteratorTests
{
    [Theory]
    [InlineData("Андрей ШНАЙДЕР", "andrei shnaider", "andrey shnayder")]
    [InlineData("Сергей Иванович Шнайдер", "sergei ivanovich shnaider", "sergey ivanovich shnayder")]
    public void BothRegisterSpellingsAreProduced(string cyrillic, string icao, string bgn)
    {
        var variants = SancMeld.Core.Matching.CyrillicTransliterator.Variants(cyrillic).Select(v => v.ToLowerInvariant()).ToList();

        Assert.Contains(icao, variants);
        Assert.Contains(bgn, variants);
    }

    [Fact]
    public void LatinTextHasNoVariants()
    {
        Assert.Empty(SancMeld.Core.Matching.CyrillicTransliterator.Variants("Andrey SCHNEIDER"));
    }
}
