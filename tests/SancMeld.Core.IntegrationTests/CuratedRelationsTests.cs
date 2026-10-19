using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Ingest;
using SancMeld.Core.Relations;
using SancMeld.Core.Search;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The internal register: a listed owner tied by hand to a group, the group to a brand,
/// a national operator to the brand. A check of the operator's name reaches the owner
/// through the chain, carrying the chain and its weakest status.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CuratedRelationsTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _source = "CUR_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _tag = "x" + Guid.NewGuid().ToString("N")[..10];
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-curated", Guid.NewGuid().ToString("N"));

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");
        db.Sources.Add(new SanctionsSource
        {
            Id = Guid.CreateVersion7(),
            JurisdictionId = jurisdiction.Id,
            Code = _source,
            Name = _source,
            Publisher = "Test",
            LegalEffect = LegalEffect.Binding,
            Format = "JSON",
            LandingPageUrl = "https://example.invalid",
            MinExpectedRecords = 1,
        });
        await db.SaveChangesAsync();
    }

    public Task DisposeAsync()
    {
        if (Directory.Exists(_snapshotRoot))
        {
            Directory.Delete(_snapshotRoot, recursive: true);
        }

        return Task.CompletedTask;
    }

    [Fact]
    public async Task AnOperatorReachesTheListedOwnerThroughTheChain()
    {
        await using (var db = fixture.CreateContext())
        {
            var outcome = await TestServices
                .Importer(fixture, db, new FakeConnector(_source)
                {
                    Records =
                    [
                        new ParsedDesignation
                        {
                            SourceRecordKey = "s1",
                            ExternalId = "s1",
                            SubjectType = SubjectType.Person,
                            Names = [new ParsedName { Value = "Sergey Ivanovich SCHNEIDER", Kind = NameType.Primary }],
                            RawRecordJson = """{"key":"s1"}""",
                        },
                    ],
                }, TestServices.Options(_snapshotRoot))
                .RunAsync(_source, force: true, CancellationToken.None);
            Assert.Equal(ImportStatus.Success, outcome.Status);
        }

        var file = $$"""
            {
              "parties": [
                { "id": "{{_tag}}-group", "name": "Group {{_tag}}" },
                { "id": "{{_tag}}-brand", "name": "Brand {{_tag}}" },
                { "id": "{{_tag}}-op", "name": "Operator {{_tag}} SK", "country": "SK", "url": "https://example.org/article" }
              ],
              "listings": [
                { "source": "{{_source}}", "externalId": "s1", "party": "{{_tag}}-group", "kind": "OwnerOf", "status": "Confirmed", "evidence": ["https://example.org/act"] }
              ],
              "links": [
                { "from": "{{_tag}}-group", "to": "{{_tag}}-brand", "kind": "OwnerOf", "status": "Confirmed" },
                { "from": "{{_tag}}-op", "to": "{{_tag}}-brand", "kind": "OperatorOf", "status": "Candidate", "evidence": ["https://example.org/article"] }
              ]
            }
            """;

        await using (var db = fixture.CreateContext())
        {
            var result = await new CuratedRelationsImporter(db, TimeProvider.System, NullLogger<CuratedRelationsImporter>.Instance)
                .ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(file)), "test");
            Assert.Equal((3, 1, 2), (result.Parties, result.ListingLinks, result.PartyLinks));
            Assert.Empty(result.Warnings);

            // Idempotent.
            await new CuratedRelationsImporter(db, TimeProvider.System, NullLogger<CuratedRelationsImporter>.Instance)
                .ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(file)), "test");
            Assert.Equal(2, await db.PartyRelations.CountAsync(r => r.From!.ExternalId.StartsWith(_tag)));
        }

        await using var check = fixture.CreateContext();
        var options = Options.Create(new SancMeldOptions());
        var queries = new DesignationQueryService(check, options);
        var screening = new ScreeningService(check, queries, new RulesScoringStrategy(), TestServices.Semantic(fixture, options), options, TimeProvider.System);
        var checkResult = await new ComplianceCheckService(screening, queries, TestServices.RelatedParties(check, queries), TimeProvider.System)
            .CheckAsync(new CheckQuery { Name = $"Operator {_tag} SK" });

        var party = Assert.Single(checkResult.RelatedParties, p => p.ExternalId == $"{_tag}-op");
        var relation = Assert.Single(party.Relations);
        Assert.Equal("Sergey Ivanovich SCHNEIDER", relation.SubjectName);
        Assert.Equal([$"Brand {_tag}", $"Group {_tag}"], relation.Via);
        Assert.Equal(LinkStatus.Candidate, relation.Status);
        Assert.Contains("→", relation.Explanation, StringComparison.Ordinal);

        // And the owner's graph runs down to the operator, the operator's tie in review.
        var listing = await check.Designations.SingleAsync(d => d.Source!.Code == _source && d.ValidTo == null);
        var graph = await new DesignationGraphService(check, queries, TestServices.RelatedParties(check, queries)).BuildAsync(listing.Id);
        Assert.NotNull(graph);
        var operatorEdge = Assert.Single(graph.Edges, e => e.Kind == nameof(RelationKind.OperatorOf));
        Assert.Equal(GraphStatus.Review, operatorEdge.Status);
        Assert.Contains(graph.Nodes, n => n.Label == $"Operator {_tag} SK");
        Assert.Equal(DecisionTarget.PartyRelation, operatorEdge.Target);
        Assert.NotNull(operatorEdge.TargetId);

        // An analyst confirms the operator link: the chain is confirmed end to end, and
        // reloading the case file does not take the confirmation back.
        var decisions = new RelationDecisionService(check, TimeProvider.System, new SancMeld.Core.Access.FullAdditionalInfoAccess());
        await decisions.DecideAsync(new RelationDecisionRequest
        {
            Target = DecisionTarget.PartyRelation,
            Id = operatorEdge.TargetId!.Value,
            Decision = LinkStatus.Confirmed,
            DecidedBy = "analyst@example.org",
            Note = "checked in ORSR",
        });

        await using (var reload = fixture.CreateContext())
        {
            await new CuratedRelationsImporter(reload, TimeProvider.System, NullLogger<CuratedRelationsImporter>.Instance)
                .ImportAsync(new MemoryStream(Encoding.UTF8.GetBytes(file)), "test");
        }

        await using (var after = fixture.CreateContext())
        {
            var link = await after.PartyRelations.SingleAsync(r => r.Id == operatorEdge.TargetId);
            Assert.Equal(LinkStatus.Confirmed, link.Status);
            Assert.Equal("analyst@example.org", link.DecidedBy);
            Assert.Equal(1, await after.RelationDecisions.CountAsync(d => d.Target == DecisionTarget.PartyRelation && d.TargetId == link.Id));

            var q2 = new DesignationQueryService(after, options);
            var s2 = new ScreeningService(after, q2, new RulesScoringStrategy(), TestServices.Semantic(fixture, options), options, TimeProvider.System);
            var confirmed = await new ComplianceCheckService(s2, q2, TestServices.RelatedParties(after, q2), TimeProvider.System)
                .CheckAsync(new CheckQuery { Name = $"Operator {_tag} SK" });
            Assert.Equal(LinkStatus.Confirmed, confirmed.RelatedParties.Single(p => p.ExternalId == $"{_tag}-op").Relations.Single().Status);

            // Rejecting the owner's tie to the group hides the chain from checks.
            var ownerRelation = await after.DesignationRelations.SingleAsync(r => r.Party!.ExternalId == $"{_tag}-group");
            await new RelationDecisionService(after, TimeProvider.System, new SancMeld.Core.Access.FullAdditionalInfoAccess()).DecideAsync(new RelationDecisionRequest
            {
                Target = DecisionTarget.ListingRelation,
                Id = ownerRelation.Id,
                Decision = LinkStatus.Rejected,
                DecidedBy = "analyst@example.org",
            });
        }

        await using var final = fixture.CreateContext();
        var q3 = new DesignationQueryService(final, options);
        var s3 = new ScreeningService(final, q3, new RulesScoringStrategy(), TestServices.Semantic(fixture, options), options, TimeProvider.System);
        var rejected = await new ComplianceCheckService(s3, q3, TestServices.RelatedParties(final, q3), TimeProvider.System)
            .CheckAsync(new CheckQuery { Name = $"Operator {_tag} SK" });
        Assert.DoesNotContain(rejected.RelatedParties, p => p.ExternalId == $"{_tag}-op" && p.Relations.Count > 0);
    }
}
