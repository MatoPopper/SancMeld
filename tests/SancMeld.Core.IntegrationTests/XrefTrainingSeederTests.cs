using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Ingest;
using SancMeld.Core.Learning;
using SancMeld.Core.Search;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// Turning publisher-declared cross-references into labelled pairs.
///
/// The corpus cannot wait for analysts — tens of decisions a day means months before a
/// model can be trained on anything. When two independent bodies say "this is the same
/// subject", each having transliterated the name their own way, that is a verified
/// positive available on day one.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class XrefTrainingSeederTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];
    private readonly string _homeCode = "XRH_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _awayCode = "XRA_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-xref", Guid.NewGuid().ToString("N"));

    private string Named(string seed) => $"{_tag}{seed}";

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");

        foreach (var code in new[] { _homeCode, _awayCode })
        {
            db.Sources.Add(new SanctionsSource
            {
                Id = Guid.CreateVersion7(),
                JurisdictionId = jurisdiction.Id,
                Code = code,
                Name = code,
                Publisher = "Test",
                LegalEffect = LegalEffect.Binding,
                Format = "JSON",
                LandingPageUrl = "https://example.invalid",
                MinExpectedRecords = 1,
            });
        }

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
    public async Task ConfirmedPairIsPositiveAndItsRivalsAreNegative()
    {
        // One reference, and whatever else the search turns up on that list. The other
        // candidates are verifiably different subjects: the publisher named which one is
        // the same, so the rest are not.
        await ImportAsync(_awayCode,
            Subject("t1", Named("gregor petrov")),
            Subject("t2", Named("gregor petrova")));

        await ImportAsync(_homeCode,
            Subject("s1", Named("gregor petrov"), xrefTo: "t1"));

        await Seed(replace: true);

        var samples = await SamplesAsync();
        Assert.Single(samples, s => s.Label);
        Assert.Contains(samples, s => !s.Label);
        Assert.All(samples, s => Assert.Equal(XrefTrainingSeeder.Origin, s.Origin));
    }

    [Fact]
    public async Task PairTheSearchCannotReachIsStillRecorded()
    {
        // The whole reason this seed is worth having. Ch'o'l Cho and CHO IL U are one
        // person written by two publishers with no shared substring, so trigram search
        // never returns the pair — and a corpus built only from what the matcher already
        // finds would teach a model nothing it does not already do.
        await ImportAsync(_awayCode, Subject("t1", Named("zzzz completely other")));
        await ImportAsync(_homeCode, Subject("s1", Named("aaaa nothing alike"), xrefTo: "t1"));

        var result = await Seed(replace: true);

        Assert.True(result.PositivesBelowSearchReach >= 1);

        // Recorded, and carrying the similarity it really has. It must sit below the
        // higher of the two retrieval thresholds: at or above it, one of the branches
        // would have returned the pair and it would not be in this count at all.
        var positive = Assert.Single(await SamplesAsync(), s => s.Label);
        var score = ScoreIn(positive.Features);

        Assert.True(
            score < new SancMeldOptions().Matching.WordSimilarityThreshold,
            $"a pair the search missed cannot score at retrieval level, but it was {score}");
    }

    [Fact]
    public async Task WeakAliasDoesNotInflateTheNameScore()
    {
        // Weak aliases are excluded from scoring by default, so reaching a match through
        // one would put a similarity into the vector that no screening could produce.
        // This failed once: the fallback path scanned every name on the subject.
        await ImportAsync(_awayCode, new ParsedDesignation
        {
            SourceRecordKey = "t1",
            ExternalId = "t1",
            SubjectType = SubjectType.Person,
            Names =
            [
                new ParsedName { Value = Named("zzzz unrelated"), Kind = NameType.Primary },
                new ParsedName { Value = Named("aaaa nothing alike"), Kind = NameType.Alias, IsWeak = true },
            ],
            RawRecordJson = """{"key":"t1"}""",
        });

        await ImportAsync(_homeCode, Subject("s1", Named("aaaa nothing alike"), xrefTo: "t1"));

        await Seed(replace: true);

        var positive = Assert.Single(await SamplesAsync(), s => s.Label);
        var nameScore = ScoreIn(positive.Features);

        Assert.True(nameScore < 0.5, $"weak alias must not raise the score, but it was {nameScore}");
    }

    [Fact]
    public async Task SeedingTwiceIsRefusedRatherThanDoubled()
    {
        // A duplicated corpus weights whatever was seeded twice, and nothing downstream
        // could tell. Refusing is recoverable; appending silently is not.
        await ImportAsync(_awayCode, Subject("t1", Named("gregor petrov")));
        await ImportAsync(_homeCode, Subject("s1", Named("gregor petrov"), xrefTo: "t1"));

        var first = await Seed(replace: true);
        var second = await Seed(fromClean: false);

        Assert.True(first.Written > 0);
        Assert.Equal(0, second.Written);
        Assert.True(second.AlreadyPresent > 0);

        var replaced = await Seed(replace: true, fromClean: false);
        Assert.Equal(first.Written, replaced.Written);
        Assert.True(replaced.Replaced > 0);
    }

    [Fact]
    public async Task ReplaceIsRefusedWhenTheInputsHaveCollapsed()
    {
        // How this was found: a migration rolled back too far took the UN source with it,
        // and --replace cheerfully deleted 224 samples to write 0. Same rule as an import,
        // for the same reason — a run that suddenly resolves nothing has lost its inputs,
        // not found fewer of them.
        await ImportAsync(_awayCode, Subject("t1", Named("gregor petrov")));
        await ImportAsync(_homeCode, Subject("s1", Named("gregor petrov"), xrefTo: "t1"));

        var seeded = await Seed(replace: true);
        Assert.True(seeded.Written > 0);

        // Everything the pairs are derived from, gone. The whole table, because the seed
        // is a global operation and the fixture database is shared — leaving other tests'
        // references in place would make what the guard sees depend on test order.
        await using (var db = fixture.CreateContext())
        {
            await db.CrossReferences.ExecuteDeleteAsync();
        }

        var refused = await Seed(replace: true, fromClean: false);

        Assert.True(refused.RefusedAsShortfall);
        Assert.Equal(0, refused.Written);
        Assert.NotEmpty(await SamplesAsync());
    }

    [Fact]
    public async Task ReferenceToSomethingThatWasNeverPublishedYieldsNoPair()
    {
        // The EU cites UN entries that do not exist and writes LYi.28 where the UN has
        // LYi.028. Those are the publisher's errors, left alone rather than repaired.
        await ImportAsync(_awayCode, Subject("t1", Named("gregor petrov")));
        await ImportAsync(_homeCode, Subject("s1", Named("gregor petrov"), xrefTo: "nosuchrecord"));

        await Seed(replace: true);

        Assert.Empty(await SamplesAsync());
    }

    [Fact]
    public async Task CorpusCarriesNoNames()
    {
        // Same rule as the adjudicated half: a training set gets copied to wherever the
        // training happens, and must not become a copy of anybody's file.
        await ImportAsync(_awayCode, Subject("t1", Named("gregor petrov")));
        await ImportAsync(_homeCode, Subject("s1", Named("gregor petrov"), xrefTo: "t1"));

        await Seed(replace: true);

        var everything = System.Text.Json.JsonSerializer.Serialize(await SamplesAsync());
        Assert.DoesNotContain(_tag, everything, StringComparison.OrdinalIgnoreCase);
    }

    private static double ScoreIn(string features) =>
        System.Text.Json.JsonDocument.Parse(features).RootElement.GetProperty("NameScore").GetDouble();

    /// <param name="replace">Passed through to the seeder: rebuild an existing corpus.</param>
    /// <param name="fromClean">
    /// Clears the cross-reference corpus first. The seed is a global operation and the
    /// fixture database is shared, so without this a test sees whatever earlier tests
    /// left behind — including a sample-to-link ratio that trips the shortfall guard for
    /// reasons having nothing to do with the test. Only the guard's own test opts out.
    /// </param>
    private async Task<XrefSeedResult> Seed(bool replace = false, bool fromClean = true)
    {
        await using var db = fixture.CreateContext();
        var options = Options.Create(new SancMeldOptions());

        if (fromClean)
        {
            await db.TrainingSamples.Where(s => s.Origin == XrefTrainingSeeder.Origin).ExecuteDeleteAsync();
        }

        return await new XrefTrainingSeeder(
                db,
                new DesignationQueryService(db, options),
                new RulesScoringStrategy(),
                options,
                TimeProvider.System)
            .SeedAsync(replace, candidatesPerPair: 25, CancellationToken.None);
    }

    private async Task<IReadOnlyList<TrainingSampleView>> SamplesAsync()
    {
        await using var db = fixture.CreateContext();
        var all = await new AdjudicationService(db, TimeProvider.System)
            .SamplesAsync(origin: XrefTrainingSeeder.Origin, limit: 10_000);

        // The fixture database is shared, so other tests' rows are in there too.
        return [.. all.Where(s => s.SourceCode == _awayCode)];
    }

    private async Task ImportAsync(string sourceCode, params ParsedDesignation[] records)
    {
        await using var db = fixture.CreateContext();

        var options = Options.Create(new SancMeldOptions
        {
            Ingest = new IngestOptions { SnapshotStoragePath = _snapshotRoot },
        });

        var outcome = await new SanctionsImportService(
                db,
                [new FakeConnector(sourceCode) { Records = [.. records] }],
                new FileSystemSnapshotStore(options),
                TestServices.EmbeddingStore(fixture, db, options),
                TimeProvider.System,
                NullLogger<SanctionsImportService>.Instance)
            .RunAsync(sourceCode, force: true, CancellationToken.None);

        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    private ParsedDesignation Subject(string key, string name, string? xrefTo = null) => new()
    {
        SourceRecordKey = key,
        ExternalId = key,
        SubjectType = SubjectType.Person,
        Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
        CrossReferences = xrefTo is null
            ? []
            : [new ParsedXref { TargetSourceCode = _awayCode, TargetExternalId = xrefTo }],
        RawRecordJson = $$"""{"key":"{{key}}"}""",
    };
}
