using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Connectors.Rpvs;
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
/// RPVS read whole against the real schema: a listed person named as a beneficial owner
/// is linked to the company by birth date and name, a namesake born the same day is not,
/// and an owner who leaves the register stops being current after the next full pass.
/// The register is stood in by canned pages in its own shape.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class RpvsSyncTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "RP_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-rpvs", Guid.NewGuid().ToString("N"));
    private readonly string _tag = Guid.NewGuid().ToString("N")[..8];
    private readonly string _ico = Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);
    private readonly int _partnerId = Random.Shared.Next(1_000_000, 9_999_999);
    private static readonly DateOnly Born = new(1971, 3, 9);

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
    public async Task AListedBeneficialOwnerIsLinkedToTheCompany()
    {
        await ImportAsync($"Oleg Borisovich KRAVETS {_tag}", Born);

        var result = await SyncAsync(Page(
            Owner($"Oleg {_tag}", "Kravets", Born, from: "2018-01-01", to: "2020-12-31"),
            Owner($"Oleg {_tag}", "Kravets", Born, from: "2021-01-01", to: null),
            Owner("Somebody", "Else", Born, from: "2018-01-01", to: null)));

        Assert.True(result.Complete);
        Assert.Single(result.ListingLinks);

        await using var db = fixture.CreateContext();
        var party = await db.RelatedParties.SingleAsync(p => p.System == RelationSystems.Rpvs && p.ExternalId == _ico);
        Assert.Equal($"Company {_tag} s.r.o.", party.Label);
        Assert.Equal("SK", party.CountryIso2);
        Assert.Equal($"https://rpvs.gov.sk/rpvs/Partner/Partner/Detail/{_partnerId}", party.SourceUrl);

        // Two entries of one person in the partner's history are one link, from the first
        // start and still current.
        var relation = await db.DesignationRelations.Include(r => r.Identity).SingleAsync(r => r.RelatedPartyId == party.Id);
        Assert.Equal(RelationKind.OwnerOf, relation.Kind);
        Assert.Equal(LinkStatus.Candidate, relation.Status);
        Assert.Equal(new DateOnly(2018, 1, 1), relation.StartDate);
        Assert.Null(relation.EndDate);
        Assert.True(relation.IsCurrent);
        Assert.True(relation.Identity!.BirthDateMatched);
        Assert.Contains("Ruská federácia", relation.Identity.Note, StringComparison.Ordinal);

        var check = await CheckAsync($"Company {_tag} s.r.o.");
        Assert.Contains(check.RelatedParties, p => p.ExternalId == _ico && p.System == RelationSystems.Rpvs);
    }

    [Fact]
    public async Task ANamesakeBornTheSameDayIsNotLinked()
    {
        await ImportAsync($"Oleg Borisovich KRAVETS {_tag}", Born);

        var result = await SyncAsync(Page(Owner($"Pavel {_tag}", "Smirnov", Born, from: "2018-01-01", to: null)));

        Assert.Empty(result.ListingLinks);
        await using var db = fixture.CreateContext();
        Assert.False(await db.RelatedParties.AnyAsync(p => p.System == RelationSystems.Rpvs && p.ExternalId == _ico));
    }

    [Fact]
    public async Task AnOwnerWhoLeavesTheRegisterIsNoLongerCurrentAfterAFullPass()
    {
        await ImportAsync($"Oleg Borisovich KRAVETS {_tag}", Born);
        await SyncAsync(Page(Owner($"Oleg {_tag}", "Kravets", Born, from: "2021-01-01", to: null)));

        var second = await SyncAsync(Page(Owner("Somebody", "Else", Born, from: "2018-01-01", to: null)));
        Assert.True(second.Complete);

        await using var db = fixture.CreateContext();
        var party = await db.RelatedParties.SingleAsync(p => p.System == RelationSystems.Rpvs && p.ExternalId == _ico);
        Assert.False((await db.DesignationRelations.SingleAsync(r => r.RelatedPartyId == party.Id)).IsCurrent);
    }

    private string Page(params string[] owners) => $$"""
        {
          "value": [
            {
              "Id": {{_partnerId}},
              "CisloVlozky": 1,
              "PartneriVerejnehoSektora": [
                { "Id": 1, "ObchodneMeno": "Company {{_tag}} s.r.o.", "Ico": "{{_ico}}", "FormaOsoby": "PravnickaOsoba", "PlatnostOd": "2018-01-01T00:00:00+01:00", "PlatnostDo": null }
              ],
              "KonecniUzivateliaVyhod": [ {{string.Join(",", owners)}} ]
            }
          ]
        }
        """;

    private static string Owner(string given, string surname, DateOnly born, string from, string? to) => $$"""
        {
          "Meno": "{{given}}", "Priezvisko": "{{surname}}",
          "DatumNarodenia": "{{born:yyyy-MM-dd}}T00:00:00+01:00",
          "JeVerejnyCinitel": false,
          "PlatnostOd": "{{from}}T00:00:00+01:00",
          "PlatnostDo": {{(to is null ? "null" : $"\"{to}T23:59:59.9+01:00\"")}},
          "StatnaPrislusnost": { "Meno": "Ruská federácia" }
        }
        """;

    private async Task<RpvsSyncResult> SyncAsync(string page)
    {
        await using var db = fixture.CreateContext();
        var service = new RpvsSyncService(
            new FakeRpvsClient(page),
            db,
            TestServices.RelatedPartyStore(db),
            Options.Create(new RpvsOptions { DelayMilliseconds = 0 }),
            TimeProvider.System,
            NullLogger<RpvsSyncService>.Instance);

        return await service.SyncAsync(null, CancellationToken.None);
    }

    private async Task ImportAsync(string name, DateOnly born)
    {
        await using var db = fixture.CreateContext();
        var record = new ParsedDesignation
        {
            SourceRecordKey = "a",
            ExternalId = "a",
            SubjectType = SubjectType.Person,
            Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
            Births = [new ParsedBirth { Year = (short)born.Year, Month = (short)born.Month, Day = (short)born.Day, Precision = DatePrecision.Day }],
            RawRecordJson = $$"""{"key":"a","name":"{{name}}"}""",
        };

        var outcome = await TestServices
            .Importer(fixture, db, new FakeConnector(_sourceCode) { Records = [record] }, TestServices.Options(_snapshotRoot))
            .RunAsync(_sourceCode, force: true, CancellationToken.None);

        Assert.Equal(ImportStatus.Success, outcome.Status);
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

    private sealed class FakeRpvsClient(string page) : IRpvsClient
    {
        public Task<JsonDocument> GetPartnersPageAsync(string? nextLink, CancellationToken ct = default) =>
            Task.FromResult(JsonDocument.Parse(page));
    }
}
