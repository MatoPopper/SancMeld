using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Ingest;
using SancMeld.Core.Search;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// Which subjects a search reaches at all — the step before any scoring, and the one where
/// a miss is unrecoverable: nothing downstream can rank a candidate that was never
/// retrieved, and in sanctions screening a missed match is a legal breach.
///
/// similarity() divides shared trigrams by the trigrams of both strings, so a surname
/// typed alone scores low against a long recorded name for reasons that have nothing to do
/// with whether it is the right person. Measured on 200 subjects: 53.5 % found by surname,
/// 13.0 % by a surname clipped by two characters. word_similarity compares the query with
/// the best-matching word instead, and the same two searches reach 98.5 % and 98.0 %.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CandidateGenerationTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "CND_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-cnd", Guid.NewGuid().ToString("N"));
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];

    private string Named(string seed) => $"{_tag}{seed}";

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");

        db.Sources.Add(new SanctionsSource
        {
            Id = Guid.CreateVersion7(),
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
    public async Task SurnameAloneReachesSubjectsWithLongRecordedNames()
    {
        // The bug this fixes: whether a surname search found somebody depended on how many
        // forenames the publisher happened to record, which is arbitrary.
        var surname = Named("burzynski");

        await ImportAsync(
            Subject("a", $"pavel {surname}"),
            Subject("b", $"pavel aleksandrovich vladimirovich {surname}"));

        var hits = await SearchAsync(surname);

        Assert.Equal(2, hits.Count);
    }

    [Fact]
    public async Task PartialSurnameStillReachesTheSubject()
    {
        // "medved" for MEDVEDEV. Someone typing what they remember is the ordinary case,
        // not an edge one.
        var surname = Named("burzynski");
        await ImportAsync(Subject("a", $"pavel andreevich {surname}"));

        var hits = await SearchAsync(surname[..^2]);

        Assert.Single(hits);
    }

    [Fact]
    public async Task FullNameSearchIsUnchanged()
    {
        // The union only ever adds candidates, so the case that already worked has to keep
        // working identically — including which spelling is reported as the match.
        var full = Named("pavel andreevich burzynski");
        await ImportAsync(Subject("a", full));

        var hits = await SearchAsync(full);
        var hit = Assert.Single(hits);

        Assert.Equal(full, hit.MatchedName);
        Assert.Equal(1.0, hit.NameScore, precision: 3);
    }

    [Fact]
    public async Task VeryShortQueryDoesNotUseTheWordBranch()
    {
        // Below the minimum length it stops being a search: "al" matches a word in 1 782
        // subjects on the real list, and with MaxCandidates capping the result the right
        // one is as likely to be cut as kept.
        await ImportAsync(Subject("a", Named("alexander petrov")));

        var options = new SancMeldOptions();
        var tooShort = new string('a', options.Matching.MinWordSimilarityQueryLength - 1);

        var hits = await SearchAsync(tooShort);

        Assert.DoesNotContain(hits, h => h.Designation.Source == _sourceCode);
    }

    [Fact]
    public async Task ScoreIsTheBetterOfTheTwoMeasures()
    {
        // A candidate retrieved by one measure but scored by the other would be ranked on
        // a number that had nothing to do with why it was found.
        var surname = Named("burzynski");
        await ImportAsync(Subject("a", $"pavel aleksandrovich vladimirovich {surname}"));

        var hit = Assert.Single(await SearchAsync(surname));

        // Whole-string similarity is far below this; only the word measure explains it.
        Assert.True(hit.NameScore > 0.5, $"expected the word measure to carry it, got {hit.NameScore}");
    }

    [Fact]
    public async Task ADelistedSubjectIsNotACandidate()
    {
        // Poland publishes its delistings as dated rows and the import keeps them, so a
        // past decision can still see what it was taken against. A search must not: the
        // obligation ended on the date in the row.
        var surname = Named("burzynski");
        var delisted = Subject("a", $"pavel {surname}") with
        {
            Status = DesignationStatus.Delisted,
            DelistedAt = new DateOnly(2022, 6, 10),
        };

        await ImportAsync(delisted, Subject("b", $"andrei {surname}"));

        var hit = Assert.Single(await SearchAsync(surname));
        Assert.Equal($"andrei {surname}", hit.MatchedName);
    }

    private async Task<IReadOnlyList<SearchHit>> SearchAsync(string query)
    {
        await using var db = fixture.CreateContext();
        var hits = await new DesignationQueryService(db, Options.Create(new SancMeldOptions()))
            .SearchAsync(query, limit: 100);

        return [.. hits.Where(h => h.Designation.Source == _sourceCode)];
    }

    private async Task ImportAsync(params ParsedDesignation[] records)
    {
        await using var db = fixture.CreateContext();

        var options = Options.Create(new SancMeldOptions
        {
            Ingest = new IngestOptions { SnapshotStoragePath = _snapshotRoot },
        });

        var outcome = await new SanctionsImportService(
                db,
                [new FakeConnector(_sourceCode) { Records = [.. records] }],
                new FileSystemSnapshotStore(options),
                TestServices.EmbeddingStore(fixture, db, options),
                TimeProvider.System,
                NullLogger<SanctionsImportService>.Instance)
            .RunAsync(_sourceCode, force: true, CancellationToken.None);

        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    private static ParsedDesignation Subject(string key, string name) => new()
    {
        SourceRecordKey = key,
        ExternalId = key,
        SubjectType = SubjectType.Person,
        Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
        RawRecordJson = $$"""{"key":"{{key}}"}""",
    };
}
