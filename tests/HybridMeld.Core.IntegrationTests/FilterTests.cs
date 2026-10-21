using HybridMeld.Core.Domain;
using HybridMeld.Core.Workspace;
using Xunit;

namespace HybridMeld.Core.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class FilterTests(DatabaseFixture fixture)
{
    [Fact]
    public void Parse_reads_lists_and_defaults()
    {
        var f = CampaignFilter.Parse(null, "sk, cz,xx,ger", "fimi,Cyber,bogus", null, true, EvidenceLevel.B, null, null, null, null, "  voice ");

        Assert.Equal(["SK", "CZ", "XX"], f.Countries);
        Assert.Equal([CampaignKind.Fimi, CampaignKind.Cyber], f.Kinds);
        Assert.False(f.TouchesSanctions);
        Assert.True(f.TouchesPep);
        Assert.Equal(EvidenceLevel.B, f.MinLevel);
        Assert.False(f.IncludeSeed);
        Assert.False(f.IncludeDrafts);
        Assert.True(f.IncludeClosed);
        Assert.Equal("voice", f.Q);
    }

    [Fact]
    public async Task List_and_facets_answer_the_filter_bar()
    {
        await using var s = fixture.CreateServices();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var filterAll = new CampaignFilter { Q = tag, IncludeDrafts = true };

        // Three cases carrying the tag: a published SK FIMI case with a D claim whose actor
        // is sanctioned; a published CZ cyber case with a confirmed A claim; a seed case.
        var fimi = await s.Campaigns.CreateAsync(new CampaignInput($"FIMI {tag}", "s", CampaignKind.Fimi, CampaignStatus.Active, ["SK"], null, null, null, null), "a@x", CancellationToken.None);
        var cyber = await s.Campaigns.CreateAsync(new CampaignInput($"Cyber {tag}", "s", CampaignKind.Cyber, CampaignStatus.Closed, ["CZ"], null, null, null, null), "a@x", CancellationToken.None);
        var seed = await s.Campaigns.CreateAsync(new CampaignInput($"Regime {tag}", "s", CampaignKind.Fimi, CampaignStatus.Active, ["EU"], null, null, null, null), "seed", CancellationToken.None);
        foreach (var slug in new[] { fimi.Slug, cyber.Slug, seed.Slug })
        {
            await s.Campaigns.SetEditorialAsync(slug, new EditorialInput(EditorialStatus.Published, null), CancellationToken.None);
        }

        var site = await s.Actors.CreateAsync(new ActorInput(ActorKind.Website, $"Site {tag}", "CZ", null, null, false, null, null), "a@x", CancellationToken.None);
        var person = await s.Actors.CreateAsync(new ActorInput(ActorKind.Person, $"Person {tag}", "SK", null, null, true, null, null), "a@x", CancellationToken.None);
        await s.Actors.SetLinksAsync(site.Actor.Id, new ActorLinkInput(Guid.NewGuid(), null), CancellationToken.None);

        var d = await s.Claims.CreateAsync(new ClaimInput(fimi.Id, person.Actor.Id, Predicate.Defends, site.Actor.Id, "D claim", EvidenceLevel.D, null, null, null,
            [new EvidenceInput("https://news.example/d", "q", null, null, null, null, null)]), "a@x", FoundBy.Analyst, CancellationToken.None);
        var a = await s.Claims.CreateAsync(new ClaimInput(cyber.Id, person.Actor.Id, Predicate.MemberOf, null, "A claim", EvidenceLevel.A, null, null, null,
            [new EvidenceInput("https://news.example/a", "q", null, null, null, null, null)]), "a@x", FoundBy.Analyst, CancellationToken.None);
        await s.Claims.DecideAsync(a.Id, new ClaimDecisionInput(ClaimStatus.Confirmed, null), "a@x", CancellationToken.None);

        // Defaults: the seed case is hidden, the two real ones show.
        var all = await s.Campaigns.ListAsync(filterAll, 50, 0, CancellationToken.None);
        Assert.Equal(new[] { cyber.Id, fimi.Id }.Order(), all.Items.Select(c => c.Id).Order());
        Assert.Equal(2, all.Total);

        var withSeed = await s.Campaigns.ListAsync(filterAll with { IncludeSeed = true }, 50, 0, CancellationToken.None);
        Assert.Equal(3, withSeed.Total);

        var sanctions = await s.Campaigns.ListAsync(filterAll with { TouchesSanctions = true }, 50, 0, CancellationToken.None);
        Assert.Equal(fimi.Id, Assert.Single(sanctions.Items).Id);

        var strong = await s.Campaigns.ListAsync(filterAll with { MinLevel = EvidenceLevel.B }, 50, 0, CancellationToken.None);
        Assert.Equal(cyber.Id, Assert.Single(strong.Items).Id);

        var confirmedD = await s.Campaigns.ListAsync(filterAll with { ConfirmedOnly = true, MinLevel = EvidenceLevel.D }, 50, 0, CancellationToken.None);
        Assert.Equal(cyber.Id, Assert.Single(confirmedD.Items).Id);

        var notClosed = await s.Campaigns.ListAsync(filterAll with { IncludeClosed = false }, 50, 0, CancellationToken.None);
        Assert.Equal(fimi.Id, Assert.Single(notClosed.Items).Id);

        var sk = await s.Campaigns.ListAsync(filterAll with { Countries = ["SK"] }, 50, 0, CancellationToken.None);
        Assert.Equal(fimi.Id, Assert.Single(sk.Items).Id);

        // Facets: kinds are counted without the kind filter, so with Cyber selected the
        // FIMI chip still says 1; countries with the kind filter applied say CZ only.
        var facets = await s.Campaigns.FacetsAsync(filterAll with { Kinds = [CampaignKind.Cyber] }, CancellationToken.None);
        Assert.Equal(1, facets.Kinds.Single(k => k.Value == CampaignKind.Fimi).Count);
        Assert.Equal(1, facets.Kinds.Single(k => k.Value == CampaignKind.Cyber).Count);
        Assert.Equal("CZ", Assert.Single(facets.Countries).Value);
        Assert.Equal(1, facets.Total);
        // The overlap chips honour the kind filter: the sanctioned actor is in the FIMI case.
        Assert.Equal(0, facets.TouchingSanctions);
        Assert.Equal(0, facets.TouchingPep);

        var open = await s.Campaigns.FacetsAsync(filterAll, CancellationToken.None);
        Assert.Equal(2, open.Total);
        Assert.Equal(1, open.TouchingSanctions);
        Assert.Equal(["CZ", "SK"], open.Countries.Select(c => c.Value).Order());

        // The feed applies the same bar to its claims: with minLevel B only the A claim shows.
        var feed = await s.Feed.SinceAsync(filterAll with { MinLevel = EvidenceLevel.B, Since = DateTimeOffset.UtcNow.AddMinutes(-5) }, 50, CancellationToken.None);
        Assert.Equal(cyber.Id, Assert.Single(feed.Campaigns).Id);
        Assert.Equal(a.Id, Assert.Single(feed.Claims).Id);
        Assert.DoesNotContain(feed.Claims, c => c.Id == d.Id);
    }
}
