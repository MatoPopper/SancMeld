using PepMeld.Core.Checks;
using PepMeld.Core.Matching;
using Xunit;

namespace PepMeld.Core.Tests;

/// <summary>
/// A query and a stored name must meet at the same string. The stored names come from a
/// source that keeps titles apart; the query comes from a form where people type them.
/// </summary>
public class PersonNamesTests
{
    [Theory]
    [InlineData("Ing. Andrej Babiš", "andrej babis")]
    [InlineData("prof. PhDr. Petr Fiala, Ph.D., LL.M.", "petr fiala")]
    [InlineData("JUDr. Jana Nováková-Horáková", "jana novakova horakova")]
    [InlineData("MUDr Vlastimil Válek, MBA", "vlastimil valek")]
    [InlineData("Petr  PAVEL", "petr pavel")]
    [InlineData("Ing.", "")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Normalize_RemovesTitlesAndAccents(string? input, string expected)
        => Assert.Equal(expected, PersonNames.Normalize(input));

    /// <summary>A title is dropped as a whole word, never as part of a name: "Ingrid" keeps its "ing", "Mgrová" its "mgr".</summary>
    [Fact]
    public void Normalize_DoesNotCutTitlesOutOfNames()
        => Assert.Equal("ingrid mgrova", PersonNames.Normalize("Ingrid Mgrová"));

    /// <summary>An initial is one letter and stays; "Ph.D." is two after normalising and must not leave a stray "d" behind.</summary>
    [Fact]
    public void Normalize_KeepsInitialsAndRemovesCompoundTitles()
    {
        Assert.Equal("j novak", PersonNames.Normalize("J. Novák"));
        Assert.Equal("jan novak", PersonNames.Normalize("Jan Novák, Ph.D., dr. h. c."));
    }

    [Fact]
    public void Score_LiftsAQueryWhollyContainedInTheStoredName()
    {
        // "Petr Fiala" against "Petr Pavel Fiala": the trigram similarity alone is below
        // the lift; both words are there, so the lift applies.
        var score = PepListMatcher.Score("petr fiala", "petr pavel fiala", 0.9);
        Assert.Equal(0.9, score);
    }

    [Fact]
    public void Score_DoesNotLiftASingleWord()
    {
        // A surname alone is contained in every namesake's name; that is not a match.
        var score = PepListMatcher.Score("fiala", "petr fiala", 0.9);
        Assert.True(score < 0.6, $"expected a plain trigram score, got {score}");
    }

    [Fact]
    public void Score_IsOneForTheSameName()
        => Assert.Equal(1.0, PepListMatcher.Score("andrej babis", "andrej babis", 0.9));

    [Fact]
    public void Score_IgnoresWordOrder()
    {
        // The source writes surname first; a caller writes it last. Trigrams are per word.
        Assert.Equal(1.0, PepListMatcher.Score("babis andrej", "andrej babis", 0.9));
    }
}
