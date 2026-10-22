using HybridMeld.Core.Domain;
using HybridMeld.Core.Seed;
using HybridMeld.Core.Workspace;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace HybridMeld.Core.IntegrationTests;

[Collection(DatabaseCollection.Name)]
public class HypothesisTests(DatabaseFixture fixture)
{
    [Fact]
    public async Task A_hypothesis_holds_indicators_and_actor_pointers_and_stays_out_of_cases_and_the_feed()
    {
        await using var s = fixture.CreateServices();
        var tag = Guid.NewGuid().ToString("N")[..8];

        var unit = await s.Actors.CreateAsync(new ActorInput(ActorKind.MilitaryUnit, $"Unit {tag}", "RU", null, null, true, null, [$"APT {tag}"]), "a@x", CancellationToken.None);
        await s.Actors.SetLinksAsync(unit.Actor.Id, new ActorLinkInput(Guid.NewGuid(), null), CancellationToken.None);

        var created = await s.Hypotheses.CreateAsync(
            new HypothesisInput($"Water {tag}", "Do published annexes help map resilience?", "Three public facts, no link between them.", ["sk", "SK", "x"], ["A breach is reported", " "], ["The authority says the annexes are harmless"], "Do not link the annexes.", null),
            "a@x", CancellationToken.None);

        Assert.Equal($"water-{tag}", created.Hypothesis.Slug);
        Assert.Equal(HypothesisStatus.Open, created.Hypothesis.Status);
        Assert.Equal(["SK"], created.Hypothesis.Countries);
        Assert.Equal(["A breach is reported"], created.ConfirmWhen);

        var withIndicator = await s.Hypotheses.AddIndicatorAsync(created.Hypothesis.Slug,
            new IndicatorInput("A contract with technical annexes was published.", EvidenceLevel.A, new DateOnly(2026, 9, 2), new EvidenceInput("https://register.example/c/1", "Poskytovanie telemetrických služieb", "CRZ", null, new DateOnly(2026, 9, 2), "sk", null)),
            "a@x", CancellationToken.None);
        Assert.Single(withIndicator.Indicators);
        Assert.True(withIndicator.Indicators[0].HasSnapshot, "the stub fetcher answers every page");

        var withActor = await s.Hypotheses.SetActorsAsync(created.Hypothesis.Slug, new HypothesisActorsInput([unit.Actor.Id]), "a@x", CancellationToken.None);
        Assert.Single(withActor.Actors);
        Assert.True(withActor.Hypothesis.TouchesSanctions);

        // The pointer shows on the actor's page; nothing shows as a claim, a case or a feed entry.
        var actor = await s.Actors.GetAsync(unit.Actor.Id, CancellationToken.None);
        Assert.NotNull(actor);
        Assert.Contains(actor.Hypotheses, h => h.Slug == created.Hypothesis.Slug);
        Assert.Empty(actor.Claims);
        Assert.Empty(actor.Campaigns);

        var feed = await s.Feed.SinceAsync(new CampaignFilter { Q = $"water-{tag}", IncludeDrafts = true }, 50, CancellationToken.None);
        Assert.DoesNotContain(feed.Campaigns, c => c.Slug == $"water-{tag}");

        var listed = await s.Hypotheses.ListAsync(HypothesisStatus.Open, tag, 10, 0, CancellationToken.None);
        Assert.Single(listed.Items);
    }

    [Fact]
    public async Task Promoting_needs_an_existing_case_and_dismissing_needs_a_note()
    {
        await using var s = fixture.CreateServices();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var h = await s.Hypotheses.CreateAsync(new HypothesisInput($"H {tag}", "Q?", "R.", null, null, null, null, null), "a@x", CancellationToken.None);

        var missing = await Assert.ThrowsAsync<WorkspaceException>(() => s.Hypotheses.DecideAsync(h.Hypothesis.Slug, new HypothesisDecisionInput(HypothesisStatus.Promoted, null, $"case-{tag}"), "a@x", CancellationToken.None));
        Assert.Equal("case_unknown", missing.Code);

        var silent = await Assert.ThrowsAsync<WorkspaceException>(() => s.Hypotheses.DecideAsync(h.Hypothesis.Slug, new HypothesisDecisionInput(HypothesisStatus.Dismissed, " ", null), "a@x", CancellationToken.None));
        Assert.Equal("note_required", silent.Code);

        await s.Campaigns.CreateAsync(new CampaignInput($"Case {tag}", "s", CampaignKind.Cyber, CampaignStatus.Active, null, null, null, null, $"case-{tag}"), "a@x", CancellationToken.None);
        var promoted = await s.Hypotheses.DecideAsync(h.Hypothesis.Slug, new HypothesisDecisionInput(HypothesisStatus.Promoted, "Written up.", $"case-{tag}"), "a@x", CancellationToken.None);
        Assert.Equal(HypothesisStatus.Promoted, promoted.Hypothesis.Status);
        Assert.Equal($"case-{tag}", promoted.Hypothesis.PromotedToSlug);
        Assert.Equal("a@x", promoted.DecidedBy);

        var reopened = await s.Hypotheses.DecideAsync(h.Hypothesis.Slug, new HypothesisDecisionInput(HypothesisStatus.Open, null, null), "b@x", CancellationToken.None);
        Assert.Equal(HypothesisStatus.Open, reopened.Hypothesis.Status);
        Assert.Null(reopened.Hypothesis.PromotedToSlug);
        Assert.Null(reopened.DecidedBy);
    }

    [Fact]
    public async Task A_hypothesis_file_links_existing_actors_only_and_imports_once()
    {
        await using var s = fixture.CreateServices();
        var tag = Guid.NewGuid().ToString("N")[..8];
        var importer = new HypothesisImportService(s.Db, s.Hypotheses, NullLogger<HypothesisImportService>.Instance);
        await s.Actors.CreateAsync(new ActorInput(ActorKind.MilitaryUnit, $"Unit {tag}", "RU", null, null, true, null, [$"APT {tag}"]), "a@x", CancellationToken.None);

        var json = $$"""
            {
              "hypothesis": { "slug": "hyp-{{tag}}", "title": "H {{tag}}", "question": "Q?", "rationale": "R.", "countries": ["SK"], "confirmWhen": ["x"], "refuteWhen": ["y"] },
              "actors": ["apt {{tag}}", "Nobody {{tag}}"],
              "indicators": [
                { "statement": "Fact one.", "level": "A", "observedOn": "2026-09-02", "evidence": { "url": "https://register.example/1", "quote": "one" } },
                { "statement": "Fact two.", "level": "B", "evidence": { "url": "https://gone.example/2", "quote": "two", "publisher": "Press" } }
              ]
            }
            """;

        var first = await importer.ImportAsync(json, "a@x", CancellationToken.None);
        var second = await importer.ImportAsync(json, "a@x", CancellationToken.None);

        Assert.True(first.Created);
        Assert.Equal(1, first.ActorsLinked);
        Assert.Equal(2, first.IndicatorsCreated);
        Assert.Contains(first.Notes, n => n.Contains($"'Nobody {tag}' not found", StringComparison.Ordinal));
        Assert.Contains(first.Notes, n => n.Contains("no snapshot of https://gone.example/2", StringComparison.Ordinal));

        Assert.False(second.Created);
        Assert.Equal(0, second.IndicatorsCreated);
        Assert.Equal(2, second.IndicatorsExisting);

        var view = await s.Hypotheses.GetAsync($"hyp-{tag}", CancellationToken.None);
        Assert.NotNull(view);
        Assert.Single(view.Actors);
        Assert.Equal(2, view.Indicators.Count);
        Assert.Empty(await s.Actors.SearchAsync($"Nobody {tag}", null, 10, CancellationToken.None));
    }
}
