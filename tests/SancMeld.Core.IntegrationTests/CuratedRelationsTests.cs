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
    }
}
