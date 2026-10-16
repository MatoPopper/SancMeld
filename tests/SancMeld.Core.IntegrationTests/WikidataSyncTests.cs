using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Connectors.Wikidata;
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
/// The whole chain, against the real schema: a listed person is found on the register
/// by birth date and name, the register's statements about them become related parties,
/// and a check of the company's name surfaces the listed owner — until the owner is
/// delisted, or an analyst rejects the tie.
///
/// Wikidata is stood in by canned SPARQL rows; the queries themselves are text and are
/// not what these tests are about.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class WikidataSyncTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "WD_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-wd", Guid.NewGuid().ToString("N"));
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];

    // Register ids of this test's own: a party is one row per Wikidata id, and two tests
    // sharing _party would share the party, its names and each other's owners.
    private readonly string _person = "Q" + Random.Shared.Next(100_000_000, 999_999_999);
    private readonly string _party = "Q" + Random.Shared.Next(100_000_000, 999_999_999);

    private Guid _sourceId;

    private string Named(string seed) => $"{seed} {_tag}";

    /// <summary>
    /// The database is shared with every other test, and "Akhmat Fund abc" is similar
    /// enough to "Akhmat Fund xyz" for the trigram index; what a test asserts on is the
    /// party it created, by the register id only it knows.
    /// </summary>
    private IReadOnlyList<CheckRelatedParty> Mine(CheckResult result) =>
        [.. result.RelatedParties.Where(p => p.ExternalId == _party)];

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");

        _sourceId = Guid.CreateVersion7();
        db.Sources.Add(new SanctionsSource
        {
            Id = _sourceId,
            JurisdictionId = jurisdiction.Id,
            Code = _sourceCode,
            Name = _sourceCode,
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
    public async Task AListedPersonIsResolvedByBirthDateAndNameAndTheirCompanyBecomesAFinding()
    {
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"), (_person, "Рамзан Кадыров", "ru"), ("Q999", "Someone Else", "en"))
            .Relation(_person, "P1830", outgoing: true, target: _party, reference: "https://example.org/source")
            .Party(_party, Named("Akhmat Fund"), "foundation", "RU", organisation: true, names: [Named("Akhmat Fund"), "Фонд Ахмата"]);

        var run = await SyncAsync(sparql);

        Assert.Equal("SUCCESS", run.Status);
        Assert.Equal(1, run.PersonsResolved);
        Assert.Equal(1, run.RelationsSeen);

        var result = await CheckAsync(Named("Akhmat Fund"));

        var party = Assert.Single(Mine(result));
        Assert.Equal(Named("Akhmat Fund"), party.Name);
        Assert.Equal("WIKIDATA", party.System);
        Assert.Equal(_party, party.ExternalId);
        Assert.Equal("RU", party.CountryIso2);
        Assert.Equal(RelatedPartyKind.Organisation, party.Kind);

        var relation = Assert.Single(party.Relations);
        Assert.Equal(RelationKind.OwnerOf, relation.Kind);
        Assert.Equal(_sourceCode, relation.Source);
        Assert.True(relation.BirthDateMatched);
        Assert.Equal(LinkStatus.Candidate, relation.IdentityStatus);
        Assert.Equal(["https://example.org/source"], relation.EvidenceUrls);
        Assert.Contains("owns", relation.Explanation, StringComparison.Ordinal);

        // The lists themselves do not know the fund: it is beside the groups, not in them.
        Assert.DoesNotContain(result.Groups.SelectMany(g => g.Subjects), s => s.Name == Named("Akhmat Fund"));
        Assert.Contains("related parties", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ACompanyIsReachableByItsCyrillicLabel()
    {
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"))
            .Relation(_person, "P112", outgoing: false, target: _party)
            .Party(_party, Named("Fight Club Akhmat"), "sports club", "RU", organisation: true, names: [Named("Fight Club Akhmat"), "Бойцовский клуб Ахмат " + _tag]);

        await SyncAsync(sparql);

        var result = await CheckAsync("Бойцовский клуб Ахмат " + _tag);

        var party = Assert.Single(Mine(result));
        Assert.Equal(RelationKind.FounderOf, party.Relations.Single().Kind);
        Assert.Equal("Бойцовский клуб Ахмат " + _tag, party.MatchedName);
    }

    [Fact]
    public async Task ANamesakeBornTheSameDayIsNotResolved()
    {
        await ImportAsync(Person("a", Named("Ivan Petrovich SIDOROV"), new DateOnly(1980, 2, 2)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1980, 2, 2), ("Q1", "Pavel Ivanov", "en"), ("Q2", "Maria Sidorova", "en"));

        var run = await SyncAsync(sparql);

        Assert.Equal(1, run.PersonsAttempted);
        Assert.Equal(0, run.PersonsResolved);

        await using var db = fixture.CreateContext();
        var identity = await db.ExternalIdentities.SingleAsync(i => i.SourceId == _sourceId);
        Assert.Null(identity.ExternalId);
        Assert.NotNull(identity.Note);

        // Recorded as a miss, so the next run does not ask the register again tomorrow.
        var pending = await TestServices.RelatedPartyStore(db).PendingPersonsAsync(RelationSystems.Wikidata, TimeSpan.FromDays(30), 100);
        Assert.DoesNotContain(pending, p => p.SourceId == _sourceId);
    }

    [Fact]
    public async Task ADelistedOwnerTakesTheCompanyWithThem()
    {
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"))
            .Relation(_person, "P1830", outgoing: true, target: _party)
            .Party(_party, Named("Akhmat Fund"), null, null, organisation: true, names: [Named("Akhmat Fund")]);

        await SyncAsync(sparql);
        Assert.Single(Mine(await CheckAsync(Named("Akhmat Fund"))));

        // The publisher withdraws the listing: a new revision, delisted.
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)) with { Status = DesignationStatus.Delisted });

        Assert.Empty(Mine(await CheckAsync(Named("Akhmat Fund"))));
    }

    [Fact]
    public async Task ARejectedIdentityHidesEveryRelationThroughIt()
    {
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"))
            .Relation(_person, "P1830", outgoing: true, target: _party)
            .Party(_party, Named("Akhmat Fund"), null, null, organisation: true, names: [Named("Akhmat Fund")]);

        await SyncAsync(sparql);

        await using (var db = fixture.CreateContext())
        {
            var identity = await db.ExternalIdentities.SingleAsync(i => i.SourceId == _sourceId);
            identity.Status = LinkStatus.Rejected;
            identity.DecidedBy = "analyst@example.org";
            identity.DecidedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync();
        }

        Assert.Empty(Mine(await CheckAsync(Named("Akhmat Fund"))));

        // And a later run does not write over the analyst's decision.
        await SyncAsync(sparql);
        await using var after = fixture.CreateContext();
        Assert.Equal(LinkStatus.Rejected, (await after.ExternalIdentities.SingleAsync(i => i.SourceId == _sourceId)).Status);
    }

    [Fact]
    public async Task AStatementTheRegisterDroppedIsClosedNotDeleted()
    {
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"))
            .Relation(_person, "P1830", outgoing: true, target: _party)
            .Party(_party, Named("Akhmat Fund"), null, null, organisation: true, names: [Named("Akhmat Fund")]);

        await SyncAsync(sparql);

        // Next week the statement is gone; relations are refreshed on a zero-day cadence here.
        sparql.ClearRelations();
        var second = await SyncAsync(sparql, refreshRelationsAfterDays: 0);
        Assert.True(second.Status == "SUCCESS", second.ErrorSummary);

        Assert.Empty(Mine(await CheckAsync(Named("Akhmat Fund"))));

        await using var db = fixture.CreateContext();
        var relation = await db.DesignationRelations.SingleAsync(r => r.Identity!.SourceId == _sourceId);
        Assert.False(relation.IsCurrent);
    }

    [Fact]
    public async Task TwoListingsOfOnePersonShareTheItemAndBothCarryItsRelations()
    {
        // The same person under two publisher records (the EU and France list most
        // Russians twice); both resolve to one item, whose statements are read once.
        await ImportAsync(
            Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)),
            Person("b", Named("KADYROV Ramzan Akhmadovitch"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"))
            .Relation(_person, "P1830", outgoing: true, target: _party)
            .Party(_party, Named("Akhmat Fund"), null, null, organisation: true, names: [Named("Akhmat Fund")]);

        var run = await SyncAsync(sparql);

        Assert.Equal("SUCCESS", run.Status);
        Assert.Equal(2, run.PersonsResolved);
        Assert.Equal(2, run.RelationsSeen);

        var party = Assert.Single(Mine(await CheckAsync(Named("Akhmat Fund"))));
        Assert.Equal(2, party.Relations.Count);
        Assert.All(party.Relations, r => Assert.Equal(RelationKind.OwnerOf, r.Kind));
    }

    [Fact]
    public async Task ARevisionThatKeptTheNamesAndDatesIsNotLookedUpAgain()
    {
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"));

        await SyncAsync(sparql);
        var queriesAfterFirst = sparql.Queries;

        // The publisher re-exports with a remark changed: a new revision, same person.
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)) with { Reason = "Head of the Chechen Republic." });

        var second = await SyncAsync(sparql);

        Assert.Equal(0, second.PersonsAttempted);
        Assert.Equal(queriesAfterFirst, sparql.Queries);

        await using var db = fixture.CreateContext();
        var designation = await db.Designations.SingleAsync(d => d.SourceId == _sourceId && d.ValidTo == null);
        var identity = await db.ExternalIdentities.SingleAsync(i => i.SourceId == _sourceId);
        Assert.Equal(designation.Id, identity.DesignationId);
        Assert.Equal(_person, identity.ExternalId);

        // A changed spelling is a new question.
        await ImportAsync(Person("a", Named("Ramzan KADYROV"), new DateOnly(1976, 10, 5)));
        var third = await SyncAsync(sparql);
        Assert.Equal(1, third.PersonsAttempted);
    }

    [Fact]
    public async Task ADetailViewListsWhatTheSubjectIsRelatedTo()
    {
        await ImportAsync(Person("a", Named("Ramzan Akhmadovitch KADYROV"), new DateOnly(1976, 10, 5)));

        var sparql = new FakeSparqlClient()
            .BornOn(new DateOnly(1976, 10, 5), (_person, Named("Ramzan Kadyrov"), "en"))
            .Relation(_person, "P1830", outgoing: true, target: _party, start: "2004-01-01T00:00:00Z")
            .Party(_party, Named("Akhmat Fund"), null, null, organisation: true, names: [Named("Akhmat Fund")]);

        await SyncAsync(sparql);

        await using var db = fixture.CreateContext();
        var designation = await db.Designations.SingleAsync(d => d.SourceId == _sourceId && d.ValidTo == null);
        var view = await TestServices.RelatedParties(db, new DesignationQueryService(db, Options.Create(new SancMeldOptions())))
            .ForDesignationAsync(designation.Id);

        var relation = Assert.Single(view.Relations);
        Assert.Equal(Named("Akhmat Fund"), relation.Party.Name);
        Assert.Equal(new DateOnly(2004, 1, 1), relation.StartDate);
        Assert.Equal("https://www.wikidata.org/wiki/" + _party, relation.Party.Url);
    }

    private async Task<RelationRun> SyncAsync(FakeSparqlClient sparql, int refreshRelationsAfterDays = 7)
    {
        await using var db = fixture.CreateContext();
        var options = Options.Create(new WikidataOptions { RefreshRelationsAfterDays = refreshRelationsAfterDays, QueryDelayMs = 0 });

        var service = new WikidataSyncService(sparql, TestServices.RelatedPartyStore(db), options, NullLogger<WikidataSyncService>.Instance);
        return await service.RunAsync(maxDates: 0, progress: null, CancellationToken.None);
    }

    private async Task<CheckResult> CheckAsync(string query)
    {
        await using var db = fixture.CreateContext();
        var options = Options.Create(new SancMeldOptions());
        var queries = new DesignationQueryService(db, options);
        var screening = new ScreeningService(db, queries, new RulesScoringStrategy(), TestServices.Semantic(fixture, options), options, TimeProvider.System);

        return await new ComplianceCheckService(screening, queries, TestServices.RelatedParties(db, queries), TimeProvider.System)
            .CheckAsync(new CheckQuery { Name = query }, CancellationToken.None);
    }

    private async Task ImportAsync(params ParsedDesignation[] records)
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(_snapshotRoot);
        var outcome = await TestServices
            .Importer(fixture, db, new FakeConnector(_sourceCode) { Records = [.. records] }, options)
            .RunAsync(_sourceCode, force: true, CancellationToken.None);

        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    private static ParsedDesignation Person(string key, string name, DateOnly born) => new()
    {
        SourceRecordKey = key,
        ExternalId = key,
        SubjectType = SubjectType.Person,
        Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
        Births = [new ParsedBirth { Year = (short)born.Year, Month = (short)born.Month, Day = (short)born.Day, Precision = DatePrecision.Day }],
        RawRecordJson = $$"""{"key":"{{key}}","name":"{{name}}"}""",
    };
}

/// <summary>
/// Answers the four queries the connector asks from canned rows, recognising each query
/// by what it selects. Nothing is parsed from the SPARQL beyond the item ids in VALUES.
/// </summary>
internal sealed class FakeSparqlClient : ISparqlClient
{
    private readonly Dictionary<DateOnly, List<(string Item, string Label, string Lang)>> _born = [];
    private readonly List<(string Item, string Prop, bool Outgoing, string Target, string? Reference, string? Start)> _relations = [];
    private readonly Dictionary<string, (string Label, string? Desc, string? Iso, bool Org, string[] Names)> _parties = [];

    public FakeSparqlClient BornOn(DateOnly date, params (string Item, string Label, string Lang)[] rows)
    {
        _born[date] = [.. rows];
        return this;
    }

    public FakeSparqlClient Relation(string item, string property, bool outgoing, string target, string? reference = null, string? start = null)
    {
        _relations.Add((item, property, outgoing, target, reference, start));
        return this;
    }

    public FakeSparqlClient Party(string id, string label, string? description, string? iso, bool organisation, string[] names)
    {
        _parties[id] = (label, description, iso, organisation, names);
        return this;
    }

    public void ClearRelations() => _relations.Clear();

    public int Queries { get; private set; }

    public Task<IReadOnlyList<SparqlRow>> QueryAsync(string sparql, CancellationToken ct = default)
    {
        Queries++;
        IReadOnlyList<SparqlRow> rows;

        if (sparql.Contains("wdt:P569", StringComparison.Ordinal))
        {
            var literal = sparql[(sparql.IndexOf('"') + 1)..][..10];
            var date = DateOnly.Parse(literal, System.Globalization.CultureInfo.InvariantCulture);
            rows = [.. _born.GetValueOrDefault(date, []).Select(r => Row(("item", Entity(r.Item)), ("label", Literal(r.Label, r.Lang)), ("lang", Literal(r.Lang))))];
        }
        else if (sparql.Contains("wdt:P1830 ?target", StringComparison.Ordinal))
        {
            var items = ItemsIn(sparql);
            rows = [.. _relations.Where(r => items.Contains(r.Item)).Select(r => Row(
                ("item", Entity(r.Item)), ("prop", Literal(r.Prop)), ("dir", Literal(r.Outgoing ? "out" : "in")),
                ("target", Entity(r.Target))))];
        }
        else if (sparql.Contains("ps:", StringComparison.Ordinal))
        {
            rows = [.. _relations
                .Where(r => sparql.Contains($"(wd:{r.Item} wd:{r.Target})", StringComparison.Ordinal))
                .Select((r, i) => Row(
                    ("item", Entity(r.Item)), ("prop", Literal(r.Prop)), ("dir", Literal(r.Outgoing ? "out" : "in")),
                    ("target", Entity(r.Target)), ("st", Literal($"stmt-{i}")),
                    ("ref", r.Reference is null ? null : Literal(r.Reference)),
                    ("start", r.Start is null ? null : Literal(r.Start))))];
        }
        else if (sparql.Contains("?human", StringComparison.Ordinal))
        {
            var items = ItemsIn(sparql);
            rows = [.. items.Where(_parties.ContainsKey).Select(id => Row(
                ("item", Entity(id)), ("enLabel", Literal(_parties[id].Label, "en")),
                ("desc", _parties[id].Desc is null ? null : Literal(_parties[id].Desc!, "en")),
                ("iso", _parties[id].Iso is null ? null : Literal(_parties[id].Iso!)),
                ("org", _parties[id].Org ? Literal("true") : null)))];
        }
        else if (sparql.Contains("?value", StringComparison.Ordinal))
        {
            rows = [];
        }
        else
        {
            var items = ItemsIn(sparql);
            rows = [.. items.Where(_parties.ContainsKey).SelectMany(id => _parties[id].Names.Select(n => Row(("item", Entity(id)), ("label", Literal(n)))))];
        }

        return Task.FromResult(rows);
    }

    private static HashSet<string> ItemsIn(string sparql)
    {
        var start = sparql.IndexOf("VALUES ?item {", StringComparison.Ordinal) + "VALUES ?item {".Length;
        var end = sparql.IndexOf('}', start);
        return sparql[start..end].Split(' ', StringSplitOptions.RemoveEmptyEntries).Select(v => v["wd:".Length..]).ToHashSet(StringComparer.Ordinal);
    }

    private static SparqlRow Row(params (string Name, SparqlValue? Value)[] values) =>
        new(values.Where(v => v.Value is not null).ToDictionary(v => v.Name, v => v.Value!, StringComparer.Ordinal));

    private static SparqlValue Entity(string id) => new("http://www.wikidata.org/entity/" + id, "uri", null, null);

    private static SparqlValue Literal(string value, string? lang = null) => new(value, "literal", lang, null);
}
