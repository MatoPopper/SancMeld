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
/// The endpoint a compliance officer actually uses, so the tests are written as the
/// questions that officer is asking: is this the person, and what do I do about it.
///
/// Two sources are set up on purpose — the point of aggregating is that one subject can
/// be listed twice, and the answer must not read like a duplicate.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ComplianceCheckTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _primaryCode = "CHK_A_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    private readonly string _secondaryCode = "CHK_B_" + Guid.NewGuid().ToString("N")[..6].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-check", Guid.NewGuid().ToString("N"));

    /// <summary>
    /// A check searches every source — that is its job — so isolating by source is not
    /// enough here the way it is for the import tests.
    ///
    /// Nor is a tagged normal name: trigram similarity is partial by design, so
    /// "Ivan Petrov 3f2a9b1c" still matches an "Ivan Petrov" another test imported — and a
    /// shared stem like "Vokrunel3f2a" matches "Vokrunel9d1e" just as happily.
    ///
    /// So the random part leads and the seed is one letter behind it. Two tests share
    /// nothing; two subjects within one test share everything but the last character.
    /// </summary>
    private readonly string _tag = Guid.NewGuid().ToString("N")[..10];

    private string Named(string seed) => $"{_tag}{seed}";

    /// <summary>A name nothing in this test resembles, for the subject the search must miss.</summary>
    private static string Unrelated() => Guid.NewGuid().ToString("N")[..12];

    public async Task InitializeAsync()
    {
        await using var db = fixture.CreateContext();
        var jurisdiction = await db.Jurisdictions.FirstAsync(j => j.Code == "EU");

        foreach (var code in new[] { _primaryCode, _secondaryCode })
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
    public async Task NameNobodyIsListedUnderComesBackClear()
    {
        await ImportAsync(_primaryCode, Subject("A", Named("a")));

        var result = await CheckAsync(new CheckQuery { Name = Unrelated() });

        Assert.Equal(CheckOutcome.NoMatch, result.Outcome);
        Assert.Empty(result.Groups);
    }

    [Fact]
    public async Task ListingThatKnowsNothingAboutTheSubjectIsUnverifiedRatherThanDismissed()
    {
        // The common case: 38 % of real listings carry no date of birth. Scoring that as a
        // near-miss would bury exactly the subjects nobody knows much about.
        await ImportAsync(_primaryCode, Subject("A", Named("a")));

        var result = await CheckAsync(new CheckQuery { Name = Named("a"), BirthYear = 1966 });

        var group = Assert.Single(result.Groups);
        Assert.Equal(CheckBand.Unverified, group.Band);
        Assert.Equal(QualifierState.UnknownOnRecord, group.Subjects[0].Evidence.BirthYear);

        // The action has to tell the officer where the answer actually is.
        Assert.Contains("your own records", group.Action, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task AgreeingDetailCorroboratesAndDisagreeingOneContradicts()
    {
        await ImportAsync(_primaryCode, Subject("A", Named("a"), birthYear: 1966));

        var agrees = await CheckAsync(new CheckQuery { Name = Named("a"), BirthYear = 1966 });
        Assert.Equal(CheckBand.Corroborated, agrees.Groups[0].Band);

        var disagrees = await CheckAsync(new CheckQuery { Name = Named("a"), BirthYear = 1980 });
        Assert.Equal(CheckBand.Contradicted, disagrees.Groups[0].Band);

        // Contradicted is still returned and still recorded — it is a decision someone may
        // have to justify later, not something to throw away.
        Assert.Single(disagrees.Groups[0].Subjects);
    }

    [Fact]
    public async Task DocumentMatchIsItsOwnAnswerAndNotASimilarity()
    {
        await ImportAsync(_primaryCode, Subject("A", Named("a"), passport: "AB-123456"));

        // A name that matches nothing, and a document that matches exactly.
        var result = await CheckAsync(new CheckQuery { Name = Unrelated(), DocumentNumber = "ab 123 456" });

        Assert.Equal(CheckOutcome.IdentifierMatch, result.Outcome);
        Assert.Equal(CheckBand.IdentifierMatch, result.Groups[0].Band);
        Assert.Contains("Escalate", result.Summary, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task SubjectListedTwiceIsOneAnswerNotTwo()
    {
        // Two publishers, one person, connected by the reference the first one states.
        await ImportAsync(_primaryCode, Subject("A", Named("a"), crossReferenceTo: ("B_SRC", "REF-1")));
        await ImportAsync(_secondaryCode, Subject("B", Named("a"), externalId: "REF-1"));

        var result = await CheckAsync(new CheckQuery { Name = Named("a") });

        var subject = Assert.Single(result.Groups.SelectMany(g => g.Subjects));

        Assert.Equal(2, subject.Listings.Count);
        Assert.Contains(subject.Listings, l => l.Source == _primaryCode);
        Assert.Contains(subject.Listings, l => l.Source == _secondaryCode);
    }

    [Fact]
    public async Task ListingTheNameSearchCannotReachIsPulledInByTheCrossReference()
    {
        // The reason aggregating is worth anything: the EU spells a man "Ch'o'l Cho" and
        // the UN spells him "CHO IL U". A query matching one will never match the other,
        // and only the publisher's own reference connects them.
        await ImportAsync(_primaryCode, Subject("A", Named("a"), crossReferenceTo: ("B_SRC", "REF-9")));
        await ImportAsync(_secondaryCode, Subject("B", Unrelated(), externalId: "REF-9"));

        var result = await CheckAsync(new CheckQuery { Name = Named("a") });

        var subject = Assert.Single(result.Groups.SelectMany(g => g.Subjects));
        Assert.Equal(2, subject.Listings.Count);
        Assert.Contains(subject.Listings, l => l.Source == _secondaryCode);
    }

    [Fact]
    public async Task AnswerCarriesNoScoreAnywhere()
    {
        // The whole point: an officer signing off cannot act on 63 %, and two of them
        // would read it differently. Nothing in this response is a number to interpret.
        await ImportAsync(_primaryCode, Subject("A", Named("a"), birthYear: 1966));

        var result = await CheckAsync(new CheckQuery { Name = Named("a"), BirthYear = 1966 });
        var json = System.Text.Json.JsonSerializer.Serialize(result);

        Assert.DoesNotContain("score", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("threshold", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CheckIsRecordedSoADecisionCanBeReconstructed()
    {
        await ImportAsync(_primaryCode, Subject("A", Named("a")));

        var result = await CheckAsync(new CheckQuery { Name = Named("a"), Reference = "PAYMENT-42" });

        await using var db = fixture.CreateContext();
        var request = await db.ScreeningRequests.SingleOrDefaultAsync(r => r.Id == result.CheckId);

        Assert.NotNull(request);
        Assert.Equal("CHECK", request!.Channel);
        Assert.Equal("PAYMENT-42", result.Reference);
    }

    [Fact]
    public async Task NameOnlyQuerySaysWhatWouldNarrowIt()
    {
        await ImportAsync(
            _primaryCode,
            Subject("A", Named("a")),
            Subject("B", Named("b")));

        var result = await CheckAsync(new CheckQuery { Name = Named("a") });

        Assert.Contains("Only a name was supplied", result.Summary, StringComparison.Ordinal);
    }

    private async Task<CheckResult> CheckAsync(CheckQuery query)
    {
        await using var db = fixture.CreateContext();
        var options = Options.Create(new SancMeldOptions());

        var queries = new DesignationQueryService(db, options);
        var screening = new ScreeningService(db, queries, new RulesScoringStrategy(), TestServices.Semantic(fixture, options), options, TimeProvider.System);

        return await new ComplianceCheckService(screening, queries, TimeProvider.System)
            .CheckAsync(query, CancellationToken.None);
    }

    private async Task ImportAsync(string sourceCode, params ParsedDesignation[] records)
    {
        await using var db = fixture.CreateContext();

        var options = Options.Create(new SancMeldOptions
        {
            Ingest = new IngestOptions { SnapshotStoragePath = _snapshotRoot },
        });

        var service = new SanctionsImportService(
            db,
            [new FakeConnector(sourceCode) { Records = [.. records] }],
            new FileSystemSnapshotStore(options),
            TestServices.EmbeddingStore(fixture, db, options),
            TimeProvider.System,
            NullLogger<SanctionsImportService>.Instance);

        var outcome = await service.RunAsync(sourceCode, CancellationToken.None);
        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    private ParsedDesignation Subject(
        string key,
        string name,
        short? birthYear = null,
        string? passport = null,
        string? externalId = null,
        (string Source, string Id)? crossReferenceTo = null) => new()
        {
            SourceRecordKey = key,
            ExternalId = externalId ?? key,
            SubjectType = SubjectType.Person,
            Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
            Births = birthYear is null ? [] : [new ParsedBirth { Year = birthYear, Precision = DatePrecision.Year }],
            Identifiers = passport is null
                ? []
                :
                [
                    new ParsedIdentifier
                    {
                        Kind = IdentifierType.Passport,
                        ValueOriginal = passport,
                        ValueNormalized = Matching.IdentifierNormalizer.Normalize(passport),
                    },
                ],
            CrossReferences = crossReferenceTo is null
                ? []
                :
                [
                    new ParsedXref
                    {
                        TargetSourceCode = _secondaryCode,
                        TargetExternalId = crossReferenceTo.Value.Id,
                    },
                ],
            RawRecordJson = $$"""{"key":"{{key}}"}""",
        };
}
