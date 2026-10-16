using Microsoft.EntityFrameworkCore;
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
/// The EU lists the Schneiders and not Svetofor; it says in their statement of reasons
/// that they own the group that runs MERE. A check of "MERE" has to reach that sentence,
/// as a mention beside the bands — not as a hit, because the company is on no list.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class MentionTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "MEN_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-mentions", Guid.NewGuid().ToString("N"));

    // A made-up brand, so no other test's text and no real listing can contain it.
    private readonly string _brand = "Zvetofar" + Guid.NewGuid().ToString("N")[..6];

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
    public async Task ACompanyNamedInAStatementOfReasonsReachesThePersonListedForIt()
    {
        await ImportAsync(
            Person("a", "Sergey Ivanovich SCHNEIDER",
                reason: $"Sergey Schneider is a Russian billionaire, co-founder and co-owner of the {_brand} Group, which runs over 2 200 discount stores under brands such as {_brand}, Mere, and MyPrice."),
            Person("b", "Ivan PETROV", reason: "Member of the State Duma."));

        var result = await CheckAsync(_brand);

        // Not a hit: no name resembles the brand.
        Assert.Empty(result.Groups);
        Assert.Equal(CheckOutcome.NoMatch, result.Outcome);

        var mention = Assert.Single(result.Mentions);
        Assert.Equal("Sergey Ivanovich SCHNEIDER", mention.SubjectName);
        Assert.Equal(_sourceCode, mention.Source);
        Assert.Equal("reason", mention.Field);
        Assert.Contains(_brand, mention.Snippet, StringComparison.Ordinal);
        Assert.Equal(1, result.MentionsTotal);
        Assert.Contains("statement of reasons", result.Summary, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryWordOfTheQueryMustOccur()
    {
        await ImportAsync(Person("a", "Sergey Ivanovich SCHNEIDER", reason: $"Co-owner of the {_brand} Group."));

        Assert.Single((await CheckAsync($"{_brand} Group")).Mentions);
        Assert.Empty((await CheckAsync($"{_brand} Holding")).Mentions);
    }

    [Fact]
    public async Task ASubjectAlreadyAmongTheHitsIsNotRepeatedAsAMention()
    {
        await ImportAsync(Person("a", $"{_brand} Trading", reason: $"Front company of the {_brand} network."));

        var result = await CheckAsync($"{_brand} Trading");

        Assert.NotEmpty(result.Groups);
        Assert.Empty(result.Mentions);
    }

    [Fact]
    public async Task ADelistedSubjectIsNotAMention()
    {
        await ImportAsync(Person("a", "Sergey Ivanovich SCHNEIDER", reason: $"Co-owner of {_brand}.") with { Status = DesignationStatus.Delisted });

        Assert.Empty((await CheckAsync(_brand)).Mentions);
    }

    [Fact]
    public async Task TheMentionsEndpointSearchesTheSameWay()
    {
        await ImportAsync(Person("a", "Sergey Ivanovich SCHNEIDER", reason: $"Co-owner of {_brand}; see also Фонд {_brand}."));

        await using var db = fixture.CreateContext();
        var page = await new DesignationQueryService(db, Options.Create(new SancMeldOptions()))
            .MentionsAsync($"Фонд {_brand}");

        var hit = Assert.Single(page.Hits);
        Assert.Equal(1, page.TotalCount);
        Assert.Contains("Фонд", hit.Snippet, StringComparison.Ordinal);
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
        var outcome = await TestServices
            .Importer(fixture, db, new FakeConnector(_sourceCode) { Records = [.. records] }, TestServices.Options(_snapshotRoot))
            .RunAsync(_sourceCode, force: true, CancellationToken.None);

        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    private static ParsedDesignation Person(string key, string name, string reason) => new()
    {
        SourceRecordKey = key,
        ExternalId = key,
        SubjectType = SubjectType.Person,
        Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
        Reason = reason,
        RawRecordJson = $$"""{"key":"{{key}}"}""",
    };
}
