using PepMeld.Core.Family;
using Xunit;

namespace PepMeld.Core.Tests;

public class FamilyResearchQueueTests
{
    private static readonly DateOnly Today = new(2026, 10, 22);

    [Fact]
    public void ACurrentFunctionComesFirstWhateverEnded()
    {
        Assert.Equal(0, FamilyResearchQueue.Standing(current: true, lastEnded: Today.AddYears(-1), Today));
    }

    [Fact]
    public void AFunctionThatEndedWithinTwelveMonthsComesBeforeAnOlderOne()
    {
        Assert.Equal(1, FamilyResearchQueue.Standing(current: false, lastEnded: Today.AddMonths(-11), Today));
        Assert.Equal(1, FamilyResearchQueue.Standing(current: false, lastEnded: Today.AddMonths(-12), Today));
        Assert.Equal(2, FamilyResearchQueue.Standing(current: false, lastEnded: Today.AddMonths(-12).AddDays(-1), Today));
    }
}
