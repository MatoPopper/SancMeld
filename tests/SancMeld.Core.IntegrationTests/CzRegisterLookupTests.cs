using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Connectors.Ares;
using SancMeld.Connectors.CzRegister;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Ingest;
using SancMeld.Core.Relations;
using SancMeld.Core.Search;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The Czech public register against the real schema, justice.cz and ARES stood in by
/// fakes: a listed person with a date of birth is searched by that date and their company
/// handed to ARES; one without is a name-only lead carrying the register's date of birth
/// and address; a company sharing the listed name is linked as such. All of it additional
/// information.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class CzRegisterLookupTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "CZ_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-czreg", Guid.NewGuid().ToString("N"));
    private readonly string _tag = "x" + Guid.NewGuid().ToString("N")[..8];
    private readonly string _ico = Random.Shared.Next(10_000_000, 99_999_999).ToString(System.Globalization.CultureInfo.InvariantCulture);

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
    public async Task APersonWithoutABirthDateIsANameOnlyLeadWithTheRegistersDateAndAddress()
    {
        var listing = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}", born: null);
        var justice = new FakeJustice
        {
            Rows = { ["Kravets"] = [Row("Oleg Kravets", new DateOnly(1960, 1, 2), "Moskva, Sadová 1, Ruská federace", "RU", "společník", $"Firma {_tag} s.r.o.", _ico)] },
        };

        var result = await LookupAsync(justice, new FakeAres(), listing);

        var line = Assert.Single(result.Links);
        Assert.Contains("born 1960-01-02", line, StringComparison.Ordinal);
        Assert.Contains("agrees with the listing (RU)", line, StringComparison.Ordinal);

        await using var db = fixture.CreateContext();
        var party = await db.RelatedParties.SingleAsync(p => p.System == RelationSystems.CzRegister && p.ExternalId == _ico);
        Assert.Equal("CZ", party.CountryIso2);
        var relation = await db.DesignationRelations.Include(r => r.Identity).SingleAsync(r => r.RelatedPartyId == party.Id);
        Assert.Equal(RelationKind.OwnerOf, relation.Kind);
        Assert.Equal(LinkStatus.Candidate, relation.Status);
        Assert.False(relation.Identity!.BirthDateMatched);

        // Hidden from everyone not allowed the additional information, like the Slovak leads.
        var queries = new DesignationQueryService(db, Options.Create(new Configuration.SancMeldOptions()));
        Assert.Empty((await TestServices.RelatedParties(db, queries, additionalInfo: false).ForDesignationAsync(listing)).Relations);
        Assert.Single((await TestServices.RelatedParties(db, queries, additionalInfo: true).ForDesignationAsync(listing)).Relations);
    }

    [Fact]
    public async Task APersonWithABirthDateIsSearchedByItAndHandedToAres()
    {
        var born = new DateOnly(1971, 3, 9);
        var listing = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}", born);
        var justice = new FakeJustice
        {
            Rows = { [$"Kravets|{born:yyyy-MM-dd}"] = [Row("Oleg Kravets", born, "Praha 1, Dlouhá 1", "CZ", "člen statutárního orgánu", $"Firma {_tag} s.r.o.", _ico)] },
        };
        var ares = new FakeAres();

        var result = await LookupAsync(justice, ares, listing);

        Assert.Contains(born, justice.BirthDatesAsked);
        Assert.Equal([_ico], ares.CompaniesRead);

        // Nothing under CZ_JUSTICE: the tie, if any, is ARES's by name and date of birth.
        await using var db = fixture.CreateContext();
        Assert.False(await db.RelatedParties.AnyAsync(p => p.System == RelationSystems.CzRegister && p.ExternalId == _ico));
        Assert.Contains(result.Notes, n => n.Contains("ARES did not link", StringComparison.Ordinal));
    }

    [Fact]
    public async Task ACompanyCarryingTheListedNameIsLinkedAsSharingIt()
    {
        var name = $"Vzor{_tag}";
        var listing = await ImportAsync(SubjectType.Organisation, $"Limited Liability Company \"{name}\"", born: null);
        var ares = new FakeAres { ByName = { [name.ToLowerInvariant()] = [(_ico, $"{name} s.r.o."), ("11111111", $"{name}ian s.r.o.")] } };

        var result = await LookupAsync(new FakeJustice(), ares, listing);

        var line = Assert.Single(result.Links);
        Assert.Contains("shares its name with", line, StringComparison.Ordinal);

        await using var db = fixture.CreateContext();
        var relation = await db.DesignationRelations.Include(r => r.Party).SingleAsync(r => r.Party!.System == RelationSystems.CzRegister && r.Party.ExternalId == _ico);
        Assert.Equal(RelationKind.SharesNameWith, relation.Kind);
    }

    private static JusticePersonRow Row(string name, DateOnly born, string address, string country, string role, string company, string ico) =>
        new(name, born, address, country, role, company, ico, false, "1");

    private async Task<CzRegisterLookupResult> LookupAsync(FakeJustice justice, FakeAres ares, Guid listing)
    {
        await using var db = fixture.CreateContext();
        var aresLookup = new AresLookupService(ares, db, TestServices.RelatedPartyStore(db), Options.Create(new AresOptions()), TimeProvider.System, NullLogger<AresLookupService>.Instance);
        var service = new CzRegisterLookupService(
            justice, ares, aresLookup, db, Options.Create(new CzRegisterOptions { DelayMilliseconds = 0 }), TimeProvider.System, NullLogger<CzRegisterLookupService>.Instance);
        return await service.LookupAsync(listing, null, CancellationToken.None);
    }

    private async Task<Guid> ImportAsync(SubjectType type, string name, DateOnly? born)
    {
        await using var db = fixture.CreateContext();
        var record = new ParsedDesignation
        {
            SourceRecordKey = "a",
            ExternalId = "a",
            SubjectType = type,
            Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
            Nationalities = type == SubjectType.Person ? [new ParsedNationality { CountryOriginal = "Russia", CountryIso2 = "RU" }] : [],
            Births = born is { } b ? [new ParsedBirth { Year = (short)b.Year, Month = (short)b.Month, Day = (short)b.Day, Precision = DatePrecision.Day }] : [],
            RawRecordJson = JsonSerializer.Serialize(new { key = "a", name }),
        };

        var outcome = await TestServices
            .Importer(fixture, db, new FakeConnector(_sourceCode) { Records = [record] }, TestServices.Options(_snapshotRoot))
            .RunAsync(_sourceCode, force: true, CancellationToken.None);
        Assert.Equal(ImportStatus.Success, outcome.Status);

        return await db.Designations.Where(d => d.Source!.Code == _sourceCode && d.ValidTo == null).Select(d => d.Id).SingleAsync();
    }

    /// <summary>Rows keyed by "surname" or "surname|yyyy-MM-dd"; anything else finds nothing.</summary>
    private sealed class FakeJustice : ICzRegisterClient
    {
        public Dictionary<string, List<JusticePersonRow>> Rows { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<DateOnly> BirthDatesAsked { get; } = [];

        public Task<JusticeSearchPage> SearchPersonAsync(string surname, string? givenName, DateOnly? birthDate, CancellationToken ct = default)
        {
            if (birthDate is { } b)
            {
                BirthDatesAsked.Add(b);
            }

            var key = birthDate is { } d ? $"{surname}|{d:yyyy-MM-dd}" : surname;
            return Task.FromResult(new JusticeSearchPage(Rows.GetValueOrDefault(key) ?? [], false, false));
        }
    }

    private sealed class FakeAres : IAresClient
    {
        public Dictionary<string, List<(string Ico, string Name)>> ByName { get; } = new(StringComparer.OrdinalIgnoreCase);
        public List<string> CompaniesRead { get; } = [];

        public Task<JsonDocument?> GetPublicRegisterAsync(string ico, CancellationToken ct = default)
        {
            CompaniesRead.Add(ico);
            return Task.FromResult<JsonDocument?>(null);
        }

        public Task<IReadOnlyList<(string Ico, string Name)>> SearchByNameAsync(string name, int max, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<(string, string)>>(ByName.GetValueOrDefault(name) ?? []);
    }
}
