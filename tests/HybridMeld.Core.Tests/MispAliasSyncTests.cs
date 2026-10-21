using System.Text.Json;
using HybridMeld.Core.Seed;
using Xunit;

namespace HybridMeld.Core.Tests;

public class MispAliasSyncTests
{
    [Theory]
    [InlineData("sandworm", "sandworm team", true)]
    [InlineData("sandworm", "sandworm", true)]
    [InlineData("apt28", "apt28 advanced persistent threat", true)]
    [InlineData("fancy bear", "fancy bear", true)]
    [InlineData("sandworm", "sandworm battalion", false)]
    [InlineData("gru", "gru unit 74455", false)]
    [InlineData("sand", "sandworm", false)]
    public void A_term_matches_a_stored_name_only_exactly_or_with_a_qualifier(string term, string stored, bool expected) =>
        Assert.Equal(expected, MispAliasSync.Matches(term, stored));

    [Fact]
    public void Clusters_carry_the_value_its_synonyms_and_the_mitre_reference()
    {
        const string json = """
            {"values":[
              {"value":"Sandworm","meta":{"synonyms":["VOODOO BEAR","G0034","Seashell Blizzard"],"refs":["https://dragos.com/x.pdf","https://attack.mitre.org/groups/G0034/"]}},
              {"value":"NoMeta"},
              {"meta":{"synonyms":["orphan"]}}
            ]}
            """;

        using var document = JsonDocument.Parse(json);
        var clusters = MispAliasSync.ParseClusters(document.RootElement);

        Assert.Equal(2, clusters.Count);
        Assert.Equal(["Sandworm", "VOODOO BEAR", "G0034", "Seashell Blizzard"], clusters[0].Names);
        Assert.Equal("https://attack.mitre.org/groups/G0034/", clusters[0].SourceUrl);
        Assert.Equal(["NoMeta"], clusters[1].Names);
        Assert.Null(clusters[1].SourceUrl);
    }
}
