using Microsoft.EntityFrameworkCore;
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
/// A new listing is the moment "what else does this touch" is asked. The import hands
/// its new listings to the registers at once; the graph puts the answer in one picture
/// with a status on every line; the feed lists what the last runs added and what each
/// addition touches.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ImpactTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly string _sourceA = "IMP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _sourceB = "IMQ_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-impact", Guid.NewGuid().ToString("N"));
    private readonly string _party = "Q" + Random.Shared.Next(100_000_000, 999_999_999);
    private readonly string _person = "Q" + Random.Shared.Next(100_000_000, 999_999_999);

    private Guid _sourceAId;

    private string Named(string seed) => $"{seed} {_tag}";

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");

        _sourceAId = Guid.CreateVersion7();
        db.Sources.Add(Source(_sourceAId, _sourceA, jurisdiction.Id, LegalEffect.Binding));
        db.Sources.Add(Source(Guid.CreateVersion7(), _sourceB, jurisdiction.Id, LegalEffect.Advisory));
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
    public async Task TheImportHandsItsNewListingsToTheRegisters()
    {
        var enricher = new RecordingEnricher();

        await ImportAsync(_sourceA, enricher, Person("a", Named("Ramzan Akhmadovitch KADYROV")));

        await using var db = fixture.CreateContext();
        var listing = await db.Designations.SingleAsync(d => d.SourceId == _sourceAId && d.ValidTo == null);
        Assert.Equal([listing.Id], enricher.Received.Single());

        // An unchanged re-import writes nothing and asks nothing.
        await ImportAsync(_sourceA, enricher, Person("a", Named("Ramzan Akhmadovitch KADYROV")));
        Assert.Single(enricher.Received);
    }

    [Fact]
    public async Task ARegisterThatIsDownDoesNotFailTheImport()
    {
        var enricher = new RecordingEnricher { Outage = new HttpRequestException("down") };

        var outcome = await ImportAsync(_sourceA, enricher, Person("a", Named("Ramzan Akhmadovitch KADYROV")));

        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    [Fact]
    public async Task TheGraphDrawsCrossReferencesPartiesAndMentionsWithTheirStatus()
    {
        // A on the binding list, B on the advisory one declaring it corresponds to A;
        // B's statement names a third listing C; a register ties A to a company.
        await ImportAsync(_sourceA, null,
            Person("a", Named("Ramzan Akhmadovitch KADYROV")),
            Person("c", Named("Adam Delimkhanov")));
        await ImportAsync(_sourceB, null,
            Person("b", Named("KADYROV Ramzan")) with
            {
                Reason = $"Head of the Chechen Republic. Associated with {Named("Adam Delimkhanov")}.",
                CrossReferences = [new ParsedXref { TargetSourceCode = _sourceA, TargetExternalId = "a" }],
            });

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"))
            .Relation(_person, "P1830", outgoing: true, target: _party)
            .Party(_party, Named("Akhmat Fund"), null, "RU", organisation: true, names: [Named("Akhmat Fund")]);
        await SyncAsync(sparql);

        await using var db = fixture.CreateContext();
        var a = await db.Designations.SingleAsync(d => d.SourceId == _sourceAId && d.SourceRecordKey == "a" && d.ValidTo == null);
        var graph = await Graphs(db).BuildAsync(a.Id);

        Assert.NotNull(graph);
        var edges = graph.Edges;

        // The advisory list's cross-reference: the publisher's own word, confirmed.
        var xref = Assert.Single(edges, e => e.Kind == "cross_reference");
        Assert.Equal(GraphStatus.Confirmed, xref.Status);
        Assert.Equal("d:" + a.Id, xref.To);

        // The register's tie, unconfirmed, reaching a binding listing: review. B, the
        // same person on the advisory list, resolves to the same item and is drawn as a
        // co-owner through the second hop.
        var owner = Assert.Single(edges, e => e.Kind == nameof(RelationKind.OwnerOf) && e.From == "d:" + a.Id);
        Assert.Contains(edges, e => e.Kind == nameof(RelationKind.OwnerOf) && e.From != "d:" + a.Id && e.Status == GraphStatus.Candidate);
        Assert.Equal(GraphStatus.Review, owner.Status);
        Assert.StartsWith("Wikidata:", owner.Evidence, StringComparison.Ordinal);
        var party = Assert.Single(graph.Nodes, n => n.Kind == GraphNodeKind.Party);
        Assert.Equal(Named("Akhmat Fund"), party.Label);
        Assert.Equal(GraphStatus.Review, party.Status);

        // B's statement names C, and both come back through B: B is drawn once.
        Assert.Single(graph.Nodes, n => n.Label == Named("KADYROV Ramzan"));
        var self = Assert.Single(graph.Nodes, n => n.Status == GraphStatus.Subject);
        Assert.Equal(a.Id, self.DesignationId);
    }

    [Fact]
    public async Task AStatementNamingAnotherListingBecomesAMentionsEdge()
    {
        await ImportAsync(_sourceA, null,
            Person("a", Named("Sergey Ivanovich SCHNEIDER")) with { Reason = $"Co-owner of the group with {Named("Andrey Ivanovich SCHNEIDER")}." },
            Person("b", Named("Andrey Ivanovich SCHNEIDER")) with { Reason = $"Brother of {Named("Sergey Ivanovich SCHNEIDER")}." });

        await using var db = fixture.CreateContext();
        var a = await db.Designations.SingleAsync(d => d.SourceId == _sourceAId && d.SourceRecordKey == "a" && d.ValidTo == null);
        var graph = await Graphs(db).BuildAsync(a.Id);

        Assert.NotNull(graph);
        var mentions = Assert.Single(graph.Edges, e => e.Kind == "mentions");
        Assert.Equal("d:" + a.Id, mentions.From);
        Assert.Equal(GraphStatus.Review, mentions.Status);

        var mentionedBy = Assert.Single(graph.Edges, e => e.Kind == "mentioned_by");
        Assert.Equal("d:" + a.Id, mentionedBy.To);
        Assert.Contains("statement of reasons", mentionedBy.Evidence, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheFeedListsWhatARunAddedAndWhatItTouches()
    {
        await ImportAsync(_sourceA, null, Person("a", Named("Andrey Ivanovich SCHNEIDER")));
        await ImportAsync(_sourceB, null,
            Person("t", Named("TORGSERVIS PL")) with { SubjectType = SubjectType.Organisation, Reason = $"Operated by {Named("Andrey Ivanovich SCHNEIDER")}." });

        await using var db = fixture.CreateContext();
        var feed = await Graphs(db).RecentAsync(runs: 50, perRun: 50);

        var run = Assert.Single(feed.Runs, r => r.Source == _sourceB);
        Assert.Equal(1, run.Inserted);
        var listing = Assert.Single(run.Listings);
        Assert.Equal(Named("TORGSERVIS PL"), listing.Name);
        Assert.Equal(1, listing.Impact.Mentions);
        Assert.Equal(GraphStatus.Review, listing.Status);
        var line = Assert.Single(listing.Impact.Lines);
        Assert.Contains(Named("Andrey Ivanovich SCHNEIDER"), line.Label, StringComparison.Ordinal);
    }

    private DesignationGraphService Graphs(SancMeldDbContext db)
    {
        var options = Options.Create(new SancMeldOptions());
        var queries = new DesignationQueryService(db, options);
        return new DesignationGraphService(db, queries, TestServices.RelatedParties(db, queries));
    }

    private async Task SyncAsync(FakeSparqlClient sparql)
    {
        await using var db = fixture.CreateContext();
        var service = new SancMeld.Connectors.Wikidata.WikidataSyncService(
            sparql, TestServices.RelatedPartyStore(db),
            Options.Create(new SancMeld.Connectors.Wikidata.WikidataOptions { QueryDelayMs = 0 }),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<SancMeld.Connectors.Wikidata.WikidataSyncService>.Instance);
        var run = await service.RunAsync(0, null, CancellationToken.None);
        Assert.Equal("SUCCESS", run.Status);
    }

    private async Task<ImportOutcome> ImportAsync(string sourceCode, IRelatedPartyEnricher? enricher, params ParsedDesignation[] records)
    {
        await using var db = fixture.CreateContext();
        var outcome = await TestServices
            .Importer(fixture, db, new FakeConnector(sourceCode) { Records = [.. records] }, TestServices.Options(_snapshotRoot), enricher: enricher)
            .RunAsync(sourceCode, force: true, CancellationToken.None);

        Assert.Equal(ImportStatus.Success, outcome.Status);
        return outcome;
    }

    private static SanctionsSource Source(Guid id, string code, Guid jurisdictionId, LegalEffect effect) => new()
    {
        Id = id,
        JurisdictionId = jurisdictionId,
        Code = code,
        Name = code,
        Publisher = "Test",
        LegalEffect = effect,
        Format = "JSON",
        LandingPageUrl = "https://example.invalid",
        MinExpectedRecords = 1,
    };

    private static ParsedDesignation Person(string key, string name) => new()
    {
        SourceRecordKey = key,
        ExternalId = key,
        SubjectType = SubjectType.Person,
        Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
        Births = [new ParsedBirth { Year = 1976, Month = 10, Day = 5, Precision = DatePrecision.Day }],
        RawRecordJson = $$"""{"key":"{{key}}","name":"{{name}}"}""",
    };

    private sealed class RecordingEnricher : IRelatedPartyEnricher
    {
        public List<IReadOnlyCollection<Guid>> Received { get; } = [];
        public Exception? Outage { get; set; }

        public Task EnrichAsync(IReadOnlyCollection<Guid> designationIds, CancellationToken ct = default)
        {
            if (Outage is { } outage)
            {
                throw outage;
            }

            Received.Add(designationIds);
            return Task.CompletedTask;
        }
    }
}
