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
/// Capturing what the analyst decided — the step that turns a screening tool into
/// something that can be measured and, eventually, learned from.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class AdjudicationTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "ADJ_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-adj", Guid.NewGuid().ToString("N"));
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
    public async Task ReasonsAreOfferedAndSplitIntoConfirmingAndRulingOut()
    {
        var reasons = await Service().ReasonsAsync();

        Assert.Contains(reasons, r => r.Code == "IDENTIFIER_MATCH" && !r.IsNegative);
        Assert.Contains(reasons, r => r.Code == "DIFFERENT_BIRTH_DATE" && r.IsNegative);
    }

    [Fact]
    public async Task OneReviewLabelsEveryCandidateItDecidedOn()
    {
        // The economics of this: a check averages 18 candidates, so a few hundred reviews
        // is a corpus of several thousand comparisons.
        var check = await CheckAsync(Named("a"), Named("a"), Named("b"));

        var result = await Service().RecordAsync(new AdjudicationRequest
        {
            CheckId = check.CheckId,
            DecidedBy = "analyst@test",
            Decisions =
            [
                Decision(check, 0, AdjudicationDecision.Match, "FULL_PROFILE_MATCH"),
                Decision(check, 1, AdjudicationDecision.NoMatch, "COMMON_NAME"),
            ],
        });

        Assert.Equal(2, result.Recorded);
        Assert.Equal(2, result.Labelled);

        var samples = await Service().SamplesAsync(origin: "ADJUDICATION", limit: 10_000);
        Assert.Contains(samples, s => s.Label && s.ReasonCode == "FULL_PROFILE_MATCH");
        Assert.Contains(samples, s => !s.Label && s.ReasonCode == "COMMON_NAME");
    }

    [Fact]
    public async Task UncertaintyIsRecordedButNotLabelled()
    {
        // "I cannot tell" is not "no". Collapsing it into a negative is how a corpus
        // quietly fills with false negatives that no one can find again.
        var check = await CheckAsync(Named("a"), Named("a"));

        var result = await Service().RecordAsync(new AdjudicationRequest
        {
            CheckId = check.CheckId,
            DecidedBy = "analyst@test",
            Decisions = [Decision(check, 0, AdjudicationDecision.NeedsMoreData)],
        });

        Assert.Equal(1, result.Recorded);
        Assert.Equal(0, result.Labelled);
    }

    [Fact]
    public async Task DecisionAboutSomethingTheCheckNeverReturnedIsRefused()
    {
        // The decision and the evidence would be out of step, and the trail would show an
        // opinion about something nobody was shown.
        var check = await CheckAsync(Named("a"), Named("a"));

        var result = await Service().RecordAsync(new AdjudicationRequest
        {
            CheckId = check.CheckId,
            DecidedBy = "analyst@test",
            Decisions =
            [
                new AdjudicationDecisionInput
                {
                    DesignationId = Guid.CreateVersion7(),
                    Decision = AdjudicationDecision.Match,
                },
            ],
        });

        Assert.Equal(0, result.Recorded);
        Assert.Single(result.Warnings);
    }

    [Fact]
    public async Task AdjudicatingAnUnknownCheckFails()
    {
        var service = Service();

        await Assert.ThrowsAsync<InvalidOperationException>(() => service.RecordAsync(new AdjudicationRequest
        {
            CheckId = Guid.CreateVersion7(),
            DecidedBy = "analyst@test",
            Decisions = [new AdjudicationDecisionInput { DesignationId = Guid.CreateVersion7(), Decision = AdjudicationDecision.Match }],
        }));
    }

    [Fact]
    public async Task CorrectionIsANewRowRatherThanAnEdit()
    {
        // An audit trail that can be edited is not one.
        var check = await CheckAsync(Named("a"), Named("a"));
        var service = Service();

        await service.RecordAsync(new AdjudicationRequest
        {
            CheckId = check.CheckId,
            DecidedBy = "analyst@test",
            Decisions = [Decision(check, 0, AdjudicationDecision.Match, "IDENTIFIER_MATCH")],
        });

        await using var db = fixture.CreateContext();
        var first = await db.Adjudications.OrderByDescending(a => a.Id).FirstAsync();

        await service.RecordAsync(new AdjudicationRequest
        {
            CheckId = check.CheckId,
            DecidedBy = "supervisor@test",
            Decisions =
            [
                Decision(check, 0, AdjudicationDecision.NoMatch, "COMMON_NAME") with { Supersedes = first.Id },
            ],
        });

        await using var after = fixture.CreateContext();
        var rows = await after.Adjudications.Where(a => a.CandidateId == first.CandidateId).ToListAsync();

        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.SupersedesId == first.Id);
        Assert.Contains(rows, r => r.Decision == AdjudicationDecision.Match);
    }

    [Fact]
    public async Task CorpusCarriesFeaturesAndNoPersonalData()
    {
        // A training set is copied to wherever the training happens. It must not become a
        // copy of somebody's customer file.
        var name = Named("a");
        var check = await CheckAsync(name, name);

        await Service().RecordAsync(new AdjudicationRequest
        {
            CheckId = check.CheckId,
            DecidedBy = "analyst@test",
            Decisions = [Decision(check, 0, AdjudicationDecision.Match)],
        });

        var samples = await Service().SamplesAsync(origin: "ADJUDICATION", limit: 10_000);
        var everything = System.Text.Json.JsonSerializer.Serialize(samples);

        Assert.DoesNotContain(name, everything, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(_tag, everything, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(samples, s => s.Features.Contains("NameScore", StringComparison.Ordinal));
    }

    [Fact]
    public async Task CorpusIsPagedByIdSoAnExportCanBeResumed()
    {
        var check = await CheckAsync(Named("a"), Named("a"), Named("b"));

        await Service().RecordAsync(new AdjudicationRequest
        {
            CheckId = check.CheckId,
            DecidedBy = "analyst@test",
            Decisions =
            [
                Decision(check, 0, AdjudicationDecision.Match),
                Decision(check, 1, AdjudicationDecision.NoMatch),
            ],
        });

        var first = await Service().SamplesAsync(limit: 1);
        var next = await Service().SamplesAsync(afterId: first[0].Id, limit: 1);

        Assert.Single(first);
        Assert.Single(next);
        Assert.True(next[0].Id > first[0].Id);
    }

    private static AdjudicationDecisionInput Decision(
        CheckResult check, int index, AdjudicationDecision decision, string? reason = null) => new()
        {
            DesignationId = check.Groups
                .SelectMany(g => g.Subjects)
                .SelectMany(s => s.Listings)
                .ElementAt(index)
                .DesignationId,
            Decision = decision,
            ReasonCode = reason,
        };

    private AdjudicationService Service() => new(fixture.CreateContext(), TimeProvider.System);

    private async Task<CheckResult> CheckAsync(string query, params string[] names)
    {
        await ImportAsync([.. names.Select((n, i) => Subject(((char)('a' + i)).ToString(), n))]);

        await using var db = fixture.CreateContext();
        var options = Options.Create(new SancMeldOptions());
        var queries = new DesignationQueryService(db, options);
        var screening = new ScreeningService(db, queries, new RulesScoringStrategy(), TestServices.Semantic(fixture, options), options, TimeProvider.System);

        return await new ComplianceCheckService(screening, queries, TestServices.RelatedParties(db, queries), TimeProvider.System)
            .CheckAsync(new CheckQuery { Name = query }, CancellationToken.None);
    }

    private async Task ImportAsync(ParsedDesignation[] records)
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
