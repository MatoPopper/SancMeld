using SancMeld.Core.Matching;
using Xunit;

namespace SancMeld.Core.Tests;

/// <summary>
/// These cases are taken from real sanctions-list data. They exist to catch the
/// single most damaging silent failure in the system: NameNormalizer drifting
/// away from the sm_normalize_name() function in the InitialSchema migration.
///
/// The integration test suite runs the same inputs through PostgreSQL and
/// asserts both produce identical output.
/// </summary>
public class NameNormalizerTests
{
    [Theory]
    [InlineData("ABÚ ZAJD UMAR DÚRDA", "abu zajd umar durda")]
    [InlineData("Sultan Hashim Ahmad Al-Ta'i", "sultan hashim ahmad al ta i")]
    [InlineData("Arabské revolučné brigády", "arabske revolucne brigady")]
    [InlineData("Soultan  Hachim   Ahmed", "soultan hachim ahmed")]
    [InlineData("O'Brien", "o brien")]
    [InlineData("  ", "")]
    [InlineData(null, "")]
    public void Normalize_ProducesCanonicalForm(string? input, string expected)
        => Assert.Equal(expected, NameNormalizer.Normalize(input));

    [Fact]
    public void Normalize_IsIdempotent()
    {
        const string input = "Vladimir Sergeevich KIRIYENKO";
        var once = NameNormalizer.Normalize(input);
        Assert.Equal(once, NameNormalizer.Normalize(once));
    }

    [Fact]
    public void Version_IsPinned()
    {
        // Bumping this constant invalidates every stored normalised name, the
        // trigram index and every previously recorded score. Deliberate act only.
        Assert.Equal(1, NameNormalizer.Version);
    }
}
