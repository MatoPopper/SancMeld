using HybridMeld.Core.Domain;
using HybridMeld.Core.Seed;
using HybridMeld.Core.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HybridMeld.Core.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class CaseImportTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task A_case_file_matches_existing_actors_creates_the_rest_and_imports_once()
    {
        await using var s = fixture.CreateServices();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var importer = new CaseImportService(s.Db, s.Campaigns, s.Actors, s.Claims, NoLinks(), NullLogger<CaseImportService>.Instance);

        // An actor the regime seed would have made, with its sanctions link, found by alias.
        var listed = await s.Actors.CreateAsync(new ActorInput(ActorKind.Website, $"Site {tag}", "CZ", null, null, true, null, [$"Site {tag} s.r.o."]), "seed", CancellationToken.None);
        await s.Actors.SetLinksAsync(listed.Actor.Id, new ActorLinkInput(Guid.NewGuid(), null), CancellationToken.None);

        var json = $$"""
            {
              "campaign": { "slug": "case-{{tag}}", "title": "Case {{tag}}", "summary": "s", "kind": "ProxyInfluence", "status": "Active", "countries": ["CZ"], "attributedTo": "owner", "editorial": "Published" },
              "actors": [
                { "key": "site", "name": "SITE {{tag}} S.R.O.", "kind": "Website", "country": "CZ", "isPublicFigure": true, "aliases": [] },
                { "key": "owner", "name": "Owner {{tag}}", "kind": "Person", "country": "UA", "isPublicFigure": true, "aliases": [] }
              ],
              "claims": [
                { "subject": "owner", "predicate": "Funds", "object": "site", "statement": "Owner funds the site.", "level": "A", "status": "Confirmed",
                  "evidence": [ { "url": "https://news.example/a", "quote": "funds the site", "publisher": "List" } ] },
                { "subject": "site", "predicate": "ListedUnder", "object": null, "statement": "Listed.", "level": "A", "status": "Candidate",
                  "evidence": [ { "url": "https://gone.example/x", "quote": "listed" } ] }
              ]
            }
            """;

        var first = await importer.ImportAsync(json, "a@x", CancellationToken.None);
        var second = await importer.ImportAsync(json, "a@x", CancellationToken.None);

        Assert.True(first.CampaignCreated);
        Assert.Equal(1, first.ActorsMatched);
        Assert.Equal(1, first.ActorsCreated);
        Assert.Equal(2, first.ClaimsCreated);
        Assert.Contains(first.Notes, n => n.Contains("no snapshot of https://gone.example/x", StringComparison.Ordinal));

        Assert.False(second.CampaignCreated);
        Assert.Equal(2, second.ActorsMatched);
        Assert.Equal(0, second.ClaimsCreated);
        Assert.Equal(2, second.ClaimsExisting);

        var view = await s.Campaigns.GetAsync($"case-{tag}", CancellationToken.None);
        Assert.NotNull(view);
        Assert.Equal(EditorialStatus.Published, view.Campaign.Editorial);
        Assert.True(view.Campaign.TouchesSanctions, "the site resolved to the linked actor, not a duplicate");
        Assert.Equal(2, view.Claims.Count);
        Assert.Equal(ClaimStatus.Confirmed, view.Claims.Single(c => c.Predicate == Predicate.Funds).Status);
        Assert.Equal(ClaimStatus.Candidate, view.Claims.Single(c => c.Predicate == Predicate.ListedUnder).Status);
        Assert.Equal("Owner " + tag, view.Campaign.AttributedTo!.Name);
    }

    private static HybridMeld.Core.Links.LinkResolver NoLinks() =>
        new(new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(), NullLogger<HybridMeld.Core.Links.LinkResolver>.Instance);

    [Fact]
    public async Task A_claim_naming_an_unknown_key_is_refused_before_anything_is_written()
    {
        await using var s = fixture.CreateServices();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var importer = new CaseImportService(s.Db, s.Campaigns, s.Actors, s.Claims, NoLinks(), NullLogger<CaseImportService>.Instance);
        var json = $$"""
            { "campaign": { "slug": "bad-{{tag}}", "title": "Bad", "summary": "s", "kind": "Cyber", "status": "Active" },
              "actors": [ { "key": "a", "name": "A {{tag}}", "kind": "Person", "isPublicFigure": true } ],
              "claims": [ { "subject": "nobody", "predicate": "Funds", "statement": "x", "level": "D", "status": "Candidate", "evidence": [ { "url": "https://news.example/a", "quote": "q" } ] } ] }
            """;

        await Assert.ThrowsAsync<InvalidDataException>(() => importer.ImportAsync(json, "a@x", CancellationToken.None));
        var listed = await s.Campaigns.ListAsync(new CampaignFilter { Q = $"bad-{tag}", IncludeDrafts = true }, 10, 0, CancellationToken.None);
        Assert.DoesNotContain(listed.Items, c => c.Slug == $"bad-{tag}" && c.ClaimCount > 0);
    }
}
