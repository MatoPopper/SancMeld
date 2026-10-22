using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.Tests;

public sealed class BirthDateResearchTests
{
    [Theory]
    [InlineData("Narodil se 12. června 1966 v Kladně.")]
    [InlineData("narozen 12. 6. 1966")]
    [InlineData("Narodil sa 12. júna 1966.")]
    [InlineData("born June 12, 1966")]
    [InlineData("(* 12.06.1966, Kladno)")]
    public void AQuoteThatStatesTheDatePasses(string quote) =>
        Assert.True(BirthDateResearch.StatesDate(quote, new DateOnly(1966, 6, 12)));

    [Theory]
    [InlineData("Narodil se 12. července 1966.")] // July, not June
    [InlineData("Narodil se v roce 1966.")]
    [InlineData("Senátor Jan Novák (1966)")]
    [InlineData("narozen 13. 6. 1966")]
    public void AQuoteThatDoesNotStateTheDateFails(string quote) =>
        Assert.False(BirthDateResearch.StatesDate(quote, new DateOnly(1966, 6, 12)));

    [Fact]
    public void ADayEqualToTheMonthMustAppearTwice()
    {
        Assert.True(BirthDateResearch.StatesDate("narozen 3. 3. 1970", new DateOnly(1970, 3, 3)));
        Assert.True(BirthDateResearch.StatesDate("narozen 3. března 1970", new DateOnly(1970, 3, 3)));
        Assert.False(BirthDateResearch.StatesDate("narozen 3. 1970", new DateOnly(1970, 3, 3)));
    }
}
