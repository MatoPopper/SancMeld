using HybridMeld.Core.Workspace;
using Xunit;

namespace HybridMeld.Core.Tests;

public class SlugTests
{
    [Theory]
    [InlineData("Voice of Europe – platby politikom", "voice-of-europe-platby-politikom")]
    [InlineData("  Režim EÚ: destabilizačné aktivity Ruska (nar. 2024/2642) ", "rezim-eu-destabilizacne-aktivity-ruska-nar-2024-2642")]
    [InlineData("regime-EU_FSF-RUSDA", "regime-eu-fsf-rusda")]
    [InlineData("???", "")]
    public void Slug_is_ascii_lowercase_hyphenated(string title, string expected) =>
        Assert.Equal(expected, CampaignService.Slugify(title));
}
