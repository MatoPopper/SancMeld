using HybridMeld.Core.Domain;
using HybridMeld.Core.Seed;
using HybridMeld.Core.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HybridMeld.Core.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class AliasSyncTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task Galaxy_aliases_attach_through_a_list_alias_and_never_chain_across_clusters()
    {
        await using var s = fixture.CreateServices();
        var marker = Guid.NewGuid().ToString("N")[..8];
        var unit = await s.Actors.CreateAsync(
            new ActorInput(ActorKind.MilitaryUnit, $"GTsST {marker}", "RU", null, null, true, null, [$"Sandworm {marker} Team"]), "a@x", CancellationToken.None);
        var sync = new MispAliasSync(s.Db, new NoClients(), TimeProvider.System, NullLogger<MispAliasSync>.Instance);

        // "Shared Name" sits on both clusters, as "Grizzly Steppe" does on APT28 and APT29 in
        // the real galaxy. The second cluster must not reach the actor through it.
        var clusters = new List<MispAliasSync.Cluster>
        {
            new($"Sandworm {marker}", [$"Sandworm {marker}", $"Seashell {marker}", $"Shared Name {marker}"], "https://attack.mitre.org/groups/G0034/"),
            new($"Other {marker}", [$"Other {marker}", $"Shared Name {marker}", $"Cozy {marker}"], null),
        };

        var first = await sync.ApplyAsync(clusters, CancellationToken.None);
        var second = await sync.ApplyAsync(clusters, CancellationToken.None);

        Assert.Equal(1, first.ActorsMatched);
        // The cluster's own value too: the list says "Sandworm … Team", the galaxy "Sandworm …".
        Assert.Equal(3, first.AliasesAdded);
        Assert.Equal(0, second.AliasesAdded);

        var view = await s.Actors.GetAsync(unit.Actor.Id, CancellationToken.None);
        var misp = view!.Aliases.Where(a => a.Origin == "misp").Select(a => a.Name).Order().ToList();
        Assert.Equal([$"Sandworm {marker}", $"Seashell {marker}", $"Shared Name {marker}"], misp);
        Assert.DoesNotContain(view.Aliases, a => a.Name.StartsWith("Cozy", StringComparison.Ordinal));
        Assert.All(view.Aliases.Where(a => a.Origin == "misp"), a => Assert.Equal("https://attack.mitre.org/groups/G0034/", a.SourceUrl));

        var hits = await s.Actors.SearchAsync($"seashell {marker}", null, 5, CancellationToken.None);
        Assert.Equal(unit.Actor.Id, Assert.Single(hits).Actor.Id);
    }

    private sealed class NoClients : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("ApplyAsync needs no network.");
    }
}
