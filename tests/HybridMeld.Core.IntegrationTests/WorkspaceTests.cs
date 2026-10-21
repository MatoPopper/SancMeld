using HybridMeld.Core.Domain;
using HybridMeld.Core.Workspace;
using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Relations;
using Xunit;

namespace HybridMeld.Core.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class WorkspaceTests(DatabaseFixture fixture)
{
    // The tests share one database; every name and slug carries a random suffix so runs
    // and tests never collide (the repository's rule for shared test databases).
    private static string Unique(string stem) => $"{stem}-{Guid.NewGuid():N}"[..Math.Min(stem.Length + 13, 60)];

    [Fact]
    public async Task Campaign_slug_comes_from_the_title_and_cannot_repeat()
    {
        await using var s = fixture.CreateServices();
        var title = Unique("Voice of Europe");

        var created = await s.Campaigns.CreateAsync(new CampaignInput(title, "Summary.", CampaignKind.ProxyInfluence, CampaignStatus.Active, ["sk", "CZ"], null, null, null, null), "a@x", CancellationToken.None);

        Assert.Equal(CampaignService.Slugify(title), created.Slug);
        Assert.Equal(EditorialStatus.Draft, created.Editorial);
        Assert.Equal(["CZ", "SK"], created.Countries);

        var again = await Assert.ThrowsAsync<WorkspaceException>(() =>
            s.Campaigns.CreateAsync(new CampaignInput("other", "Summary.", CampaignKind.Fimi, CampaignStatus.Active, null, null, null, null, created.Slug), "a@x", CancellationToken.None));
        Assert.Equal("slug_taken", again.Code);
        Assert.Equal(409, again.Status);
    }

    [Fact]
    public async Task Withdrawing_a_campaign_needs_a_reason()
    {
        await using var s = fixture.CreateServices();
        var created = await s.Campaigns.CreateAsync(new CampaignInput(Unique("Case"), "Summary.", CampaignKind.Cyber, CampaignStatus.Active, null, null, null, null, null), "a@x", CancellationToken.None);

        var refused = await Assert.ThrowsAsync<WorkspaceException>(() => s.Campaigns.SetEditorialAsync(created.Slug, new EditorialInput(EditorialStatus.Withdrawn, " "), CancellationToken.None));
        Assert.Equal("reason_required", refused.Code);

        var published = await s.Campaigns.SetEditorialAsync(created.Slug, new EditorialInput(EditorialStatus.Published, null), CancellationToken.None);
        Assert.NotNull(published.PublishedAt);
    }

    [Fact]
    public async Task Actor_search_reaches_an_alias_in_normal_form()
    {
        await using var s = fixture.CreateServices();
        var marker = Guid.NewGuid().ToString("N")[..8];

        var unit = await s.Actors.CreateAsync(new ActorInput(ActorKind.MilitaryUnit, $"GRU Unit 74455 {marker}", "RU", null, "cyber unit", true, null, [$"Sandworm {marker}", $"Sandworm {marker}"]), "a@x", CancellationToken.None);

        var hits = await s.Actors.SearchAsync($"SANDWORM {marker}", null, 10, CancellationToken.None);
        var hit = Assert.Single(hits);
        Assert.Equal(unit.Actor.Id, hit.Actor.Id);
        Assert.Equal($"Sandworm {marker}", hit.MatchedAlias);
        Assert.Single(unit.Aliases);
    }

    [Fact]
    public async Task A_claim_needs_a_quote_and_starts_as_a_candidate()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);

        var noEvidence = await Assert.ThrowsAsync<WorkspaceException>(() => s.Claims.CreateAsync(
            new ClaimInput(campaign.Id, politician.Id, Predicate.Defends, website.Id, "Obhajoval web.", EvidenceLevel.D, null, null, null, []), "a@x", FoundBy.Analyst, CancellationToken.None));
        Assert.Equal("evidence_required", noEvidence.Code);

        var noQuote = await Assert.ThrowsAsync<WorkspaceException>(() => s.Claims.CreateAsync(
            new ClaimInput(campaign.Id, politician.Id, Predicate.Defends, website.Id, "Obhajoval web.", EvidenceLevel.D, null, null, null,
                [new EvidenceInput("https://news.example/a", "  ", null, null, null, null, null)]), "a@x", FoundBy.Analyst, CancellationToken.None));
        Assert.Equal("quote_required", noQuote.Code);

        var claim = await s.Claims.CreateAsync(
            new ClaimInput(campaign.Id, politician.Id, Predicate.Defends, website.Id, "Politik obhajoval web v rozhovore.", EvidenceLevel.D, new DateOnly(2024, 3, 1), null, null,
                [new EvidenceInput("https://news.example/a", "„Je to slobodné médium,“ povedal.", "News", null, new DateOnly(2024, 3, 1), "sk", null)]), "a@x", FoundBy.Analyst, CancellationToken.None);

        Assert.Equal(ClaimStatus.Candidate, claim.Status);
        Assert.Equal(EvidenceLevel.D, claim.Level);
        var evidence = Assert.Single(claim.Evidence);
        Assert.True(evidence.HasSnapshot, "the stub page is fetched and kept");
        Assert.Equal("Stub page", evidence.Title);
    }

    [Fact]
    public async Task Evidence_whose_page_is_gone_is_kept_without_a_snapshot()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);

        var claim = await s.Claims.CreateAsync(
            new ClaimInput(campaign.Id, website.Id, Predicate.Amplifies, politician.Id, "Web šíril jeho výroky.", EvidenceLevel.C, null, null, null,
                [new EvidenceInput("https://gone.example/deleted", "Citát z vymazanej stránky.", null, null, null, null, "https://web.archive.org/web/2024/https://gone.example/deleted")]), "a@x", FoundBy.Analyst, CancellationToken.None);

        var evidence = Assert.Single(claim.Evidence);
        Assert.False(evidence.HasSnapshot);
        Assert.Equal("gone.example", evidence.Publisher);
        Assert.NotNull(evidence.ArchiveUrl);
    }

    [Fact]
    public async Task Decisions_are_recorded_and_withdrawing_needs_a_note()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);
        var claim = await CreateClaimAsync(s, campaign, politician, website);

        var confirmed = await s.Claims.DecideAsync(claim.Id, new ClaimDecisionInput(ClaimStatus.Confirmed, null), "b@x", CancellationToken.None);
        Assert.Equal(ClaimStatus.Confirmed, confirmed.Status);
        Assert.Equal("b@x", confirmed.DecidedBy);

        var refused = await Assert.ThrowsAsync<WorkspaceException>(() => s.Claims.DecideAsync(claim.Id, new ClaimDecisionInput(ClaimStatus.Withdrawn, null), "b@x", CancellationToken.None));
        Assert.Equal("note_required", refused.Code);

        var back = await s.Claims.DecideAsync(claim.Id, new ClaimDecisionInput(ClaimStatus.Candidate, "second look"), "b@x", CancellationToken.None);
        Assert.Equal(ClaimStatus.Candidate, back.Status);
        Assert.Null(back.DecidedBy);

        var decisions = await s.Db.ClaimDecisions.Where(d => d.ClaimId == claim.Id).OrderBy(d => d.Id).ToListAsync();
        Assert.Equal([ClaimStatus.Confirmed, ClaimStatus.Candidate], decisions.Select(d => d.Decision));
        Assert.Equal([ClaimStatus.Candidate, ClaimStatus.Confirmed], decisions.Select(d => d.PreviousStatus));
    }

    [Fact]
    public async Task Campaign_view_counts_claims_and_actors_and_flags_sanctions_links()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);
        await CreateClaimAsync(s, campaign, politician, website);

        // No sanctions database in the tests: the id is stored unverified, which is the development path.
        var designationId = Guid.NewGuid();
        var linked = await s.Actors.SetLinksAsync(website.Id, new ActorLinkInput(designationId, null), CancellationToken.None);
        Assert.Equal(designationId, linked.Actor.SanctionDesignationId);
        Assert.Null(linked.LinkVerifiedAt);

        var view = await s.Campaigns.GetAsync(campaign.Slug, CancellationToken.None);
        Assert.NotNull(view);
        Assert.Equal(1, view.Campaign.ClaimCount);
        Assert.Equal(0, view.Campaign.ConfirmedCount);
        Assert.Equal(2, view.Campaign.ActorCount);
        Assert.True(view.Campaign.TouchesSanctions);
        Assert.False(view.Campaign.TouchesPep);
    }

    [Fact]
    public async Task Graph_draws_actors_as_nodes_claims_as_edges_and_listings_with_a_route_out()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);
        var claim = await CreateClaimAsync(s, campaign, politician, website);
        var designationId = Guid.NewGuid();
        await s.Actors.SetLinksAsync(website.Id, new ActorLinkInput(designationId, null), CancellationToken.None);

        var graph = await s.Graphs.ForCampaignAsync(campaign.Slug, CancellationToken.None);

        Assert.NotNull(graph);
        var actorNodes = graph.Nodes.Where(n => n.Kind == GraphNodeKind.Actor).ToList();
        Assert.Equal(2, actorNodes.Count);
        Assert.All(actorNodes, n => Assert.StartsWith("/hybrid/actors/", n.Route));

        var listing = Assert.Single(graph.Nodes, n => n.Kind == GraphNodeKind.Listing);
        Assert.Equal($"/designations/{designationId}", listing.Route);
        Assert.Equal(designationId, listing.DesignationId);

        var edge = Assert.Single(graph.Edges, e => e.Target == DecisionTarget.Claim);
        Assert.Equal(claim.Id, edge.TargetId);
        Assert.Equal("Defends", edge.Kind);
        Assert.Equal(GraphStatus.Candidate, edge.Status);
        Assert.StartsWith("[D] ", edge.Evidence);
        Assert.Single(graph.Edges, e => e.Kind == "same_as" && e.To == "d:" + designationId);

        var around = await s.Graphs.ForActorAsync(politician.Id, CancellationToken.None);
        Assert.NotNull(around);
        Assert.Equal(GraphStatus.Subject, Assert.Single(around.Nodes, n => n.Id == "a:" + politician.Id).Status);
    }

    [Fact]
    public async Task Watched_source_is_fetched_into_the_inbox_once_and_a_claim_links_the_item()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);
        var code = Unique("FEED").Replace('-', '_').ToUpperInvariant();

        var source = await s.Sources.CreateAsync(new SourceInput(code, "Test feed", null, "https://feed.example/rss", SourceKind.Media, FetchKind.Rss, false, null, null, null), "a@x", CancellationToken.None);
        Assert.Equal(24, source.RefreshHours);

        var first = await s.Fetch.RunAsync(code, CancellationToken.None);
        var second = await s.Fetch.RunAsync(code, CancellationToken.None);
        Assert.Equal(RunStatus.Success, first.Status);
        Assert.Equal(2, first.ItemsNew);
        Assert.Equal(0, second.ItemsNew);
        Assert.Empty(await s.Fetch.DueAsync(CancellationToken.None) is var due ? due.Where(d => d == code) : []);

        var inbox = await s.Sources.InboxAsync(SourceItemStatus.New, code, null, 10, 0, CancellationToken.None);
        Assert.Equal(2, inbox.Total);
        var item = inbox.Items.First(i => i.ExternalId == "1");
        Assert.Equal("A sentence about a channel.", item.Text);

        var claim = await s.Claims.CreateAsync(
            new ClaimInput(campaign.Id, website.Id, Predicate.Amplifies, politician.Id, "Z položky schránky.", EvidenceLevel.C, null, null, item.Id, null), "a@x", FoundBy.Analyst, CancellationToken.None);

        var evidence = Assert.Single(claim.Evidence);
        Assert.Equal("https://feed.example/1", evidence.Url);
        Assert.Equal("A sentence about a channel.", evidence.Quote);
        Assert.True(evidence.HasSnapshot);

        var linked = await s.Sources.GetItemAsync(item.Id, CancellationToken.None);
        Assert.Equal(SourceItemStatus.Linked, linked!.Status);
        Assert.Equal(1, linked.LinkedClaims);

        var byHand = await Assert.ThrowsAsync<WorkspaceException>(() => s.Sources.TriageAsync(item.Id, new TriageInput(SourceItemStatus.Linked, null), "a@x", CancellationToken.None));
        Assert.Equal("linked_by_claim", byHand.Code);
    }

    [Fact]
    public async Task Telegram_source_gets_its_code_from_the_channel_and_fetches_hourly()
    {
        await using var s = fixture.CreateServices();
        var channel = "chan" + Guid.NewGuid().ToString("N")[..6];

        var source = await s.Sources.CreateAsync(new SourceInput(null, "A channel", null, $"https://t.me/{channel}", SourceKind.Channel, FetchKind.Telegram, false, null, null, null), "a@x", CancellationToken.None);

        Assert.Equal("TG_" + channel.ToUpperInvariant(), source.Code);
        Assert.Equal(1, source.RefreshHours);

        var run = await s.Fetch.RunAsync(source.Code, CancellationToken.None);
        Assert.Equal(RunStatus.Success, run.Status);
        Assert.Equal(1, run.ItemsNew);
    }

    [Fact]
    public async Task Feed_shows_published_cases_only()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);
        await CreateClaimAsync(s, campaign, politician, website);

        var before = await s.Feed.SinceAsync(DateTimeOffset.UtcNow.AddMinutes(-5), 500, CancellationToken.None);
        Assert.DoesNotContain(before.Campaigns, c => c.Id == campaign.Id);
        Assert.DoesNotContain(before.Claims, c => c.CampaignId == campaign.Id);

        await s.Campaigns.SetEditorialAsync(campaign.Slug, new EditorialInput(EditorialStatus.Published, null), CancellationToken.None);

        var after = await s.Feed.SinceAsync(DateTimeOffset.UtcNow.AddMinutes(-5), 500, CancellationToken.None);
        Assert.Contains(after.Campaigns, c => c.Id == campaign.Id);
        Assert.Contains(after.Claims, c => c.CampaignId == campaign.Id);
    }

    [Fact]
    public async Task Forgetting_an_actor_removes_their_claims_and_keeps_the_decisions()
    {
        await using var s = fixture.CreateServices();
        var (campaign, politician, website) = await SeedCaseAsync(s);
        var claim = await CreateClaimAsync(s, campaign, politician, website);
        await s.Claims.DecideAsync(claim.Id, new ClaimDecisionInput(ClaimStatus.Confirmed, null), "b@x", CancellationToken.None);

        var removed = await s.Actors.ForgetAsync(politician.Id, CancellationToken.None);

        Assert.Equal(1, removed);
        Assert.Null(await s.Actors.GetAsync(politician.Id, CancellationToken.None));
        Assert.Null(await s.Claims.GetAsync(claim.Id, CancellationToken.None));
        Assert.Single(await s.Db.ClaimDecisions.Where(d => d.ClaimId == claim.Id).ToListAsync());
    }

    private static async Task<(CampaignSummary Campaign, ActorRef Politician, ActorRef Website)> SeedCaseAsync(DatabaseFixture.Services s)
    {
        var campaign = await s.Campaigns.CreateAsync(new CampaignInput(Unique("Case"), "Summary.", CampaignKind.ProxyInfluence, CampaignStatus.Active, ["SK"], null, null, null, null), "a@x", CancellationToken.None);
        var politician = await s.Actors.CreateAsync(new ActorInput(ActorKind.Person, Unique("Politik"), "SK", null, "poslanec", true, null, null), "a@x", CancellationToken.None);
        var website = await s.Actors.CreateAsync(new ActorInput(ActorKind.Website, Unique("Web"), "CZ", null, null, false, null, null), "a@x", CancellationToken.None);
        return (campaign, politician.Actor, website.Actor);
    }

    private static Task<ClaimView> CreateClaimAsync(DatabaseFixture.Services s, CampaignSummary campaign, ActorRef subject, ActorRef @object) =>
        s.Claims.CreateAsync(
            new ClaimInput(campaign.Id, subject.Id, Predicate.Defends, @object.Id, "Politik obhajoval web.", EvidenceLevel.D, null, null, null,
                [new EvidenceInput("https://news.example/a", "Citát.", null, null, null, null, null)]), "a@x", FoundBy.Analyst, CancellationToken.None);
}
