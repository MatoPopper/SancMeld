using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SancMeld.Connectors.SkRegister;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Ingest;
using SancMeld.Core.Relations;
using SancMeld.Core.Search;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// The Slovak business register against the real schema, with ORSR and RPO stood in by a
/// fake: a listed person found as a shareholder is linked to the company as a lead with
/// the country of their address, a namesake with another patronymic is not, a company
/// sharing the listed name is linked as such — and none of it is shown to a caller who is
/// not allowed the additional information.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class SkRegisterLookupTests(DatabaseFixture fixture) : IAsyncLifetime
{
    private readonly string _sourceCode = "SK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
    private readonly string _snapshotRoot = Path.Combine(Path.GetTempPath(), "sancmeld-skreg", Guid.NewGuid().ToString("N"));
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
    public async Task AListedPersonFoundAsAShareholderIsALeadWithTheCountryOfTheAddress()
    {
        var listing = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}");
        var fake = PersonRegister();

        var result = await LookupAsync(fake, listing);

        Assert.Single(result.Links);

        await using var db = fixture.CreateContext();
        var party = await db.RelatedParties.SingleAsync(p => p.System == RelationSystems.SkRegister && p.ExternalId == _ico);
        Assert.Equal($"Firma {_tag} s. r. o.", party.Label);
        Assert.Equal("SK", party.CountryIso2);

        var relation = await db.DesignationRelations.Include(r => r.Identity).SingleAsync(r => r.RelatedPartyId == party.Id);
        Assert.Equal(RelationKind.OwnerOf, relation.Kind);
        Assert.Equal(LinkStatus.Candidate, relation.Status);
        Assert.Equal(new DateOnly(2019, 8, 6), relation.StartDate);
        Assert.Contains("Ruská federácia", relation.Note, StringComparison.Ordinal);
        Assert.Contains("agrees with the listing (RU)", relation.Note, StringComparison.Ordinal);
        Assert.False(relation.Identity!.BirthDateMatched);
        Assert.Equal(RelationSystems.SkRegister, relation.Identity.System);

        // The other Kravets in ORSR has another given name, and the one in the company with
        // another patronymic is not covered by the listed name: neither is read or stored.
        Assert.DoesNotContain(fake.Extracts, e => e == "99");
    }

    [Fact]
    public async Task OnlyAUserAllowedTheAdditionalInformationSeesTheLead()
    {
        var listing = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}");
        await LookupAsync(PersonRegister(), listing);

        await using var db = fixture.CreateContext();
        var queries = new DesignationQueryService(db, Options.Create(new Configuration.SancMeldOptions()));

        var allowed = TestServices.RelatedParties(db, queries, additionalInfo: true);
        var everyone = TestServices.RelatedParties(db, queries, additionalInfo: false);

        Assert.Single((await allowed.ForDesignationAsync(listing)).Relations);
        Assert.Empty((await everyone.ForDesignationAsync(listing)).Relations);

        Assert.Contains(await allowed.SearchAsync($"Firma {_tag}"), h => h.Party.ExternalId == _ico);
        Assert.DoesNotContain(await everyone.SearchAsync($"Firma {_tag}"), h => h.Party.ExternalId == _ico);

        var hiddenGraph = await new DesignationGraphService(db, queries, everyone).BuildAsync(listing);
        Assert.DoesNotContain(hiddenGraph!.Nodes, n => n.Label == $"Firma {_tag} s. r. o.");

        var graph = await new DesignationGraphService(db, queries, allowed).BuildAsync(listing);
        var edge = Assert.Single(graph!.Edges, e => e.Kind == nameof(RelationKind.OwnerOf));

        // A name in a register without dates of birth stays amber on a binding listing.
        Assert.Equal(GraphStatus.Candidate, edge.Status);
        Assert.Contains("name match only", edge.Evidence, StringComparison.Ordinal);

        // And it cannot be decided by someone who cannot see it.
        var relationId = (await allowed.ForDesignationAsync(listing)).Relations[0].Id;
        var blind = new RelationDecisionService(db, TimeProvider.System, new Access.NoAdditionalInfoAccess());
        await Assert.ThrowsAsync<KeyNotFoundException>(() => blind.DecideAsync(new RelationDecisionRequest
        {
            Target = DecisionTarget.ListingRelation,
            Id = relationId,
            Decision = LinkStatus.Rejected,
            DecidedBy = "test",
        }));
    }

    [Fact]
    public async Task TheReviewQueueListsTheLeadOnlyForThoseAllowed()
    {
        var listing = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}");
        await LookupAsync(PersonRegister(), listing);

        await using var db = fixture.CreateContext();
        var queries = new DesignationQueryService(db, Options.Create(new Configuration.SancMeldOptions()));

        var allowed = await new ReviewQueueService(db, TestServices.RelatedParties(db, queries, additionalInfo: true))
            .ListAsync(LinkStatus.Candidate, RelationSystems.SkRegister, null, ReviewQueueService.MaxItems);
        var item = Assert.Single(allowed.Items, i => i.Listing.DesignationId == listing);
        Assert.Equal(ReviewPriority.Candidate, item.Priority);
        Assert.True(item.CountryAgrees);
        Assert.Equal(_ico, item.Party.ExternalId);

        // The country switch: a Slovak lead is under SK and not under CZ.
        var czOnly = await new ReviewQueueService(db, TestServices.RelatedParties(db, queries, additionalInfo: true))
            .ListAsync(LinkStatus.Candidate, null, "CZ", ReviewQueueService.MaxItems);
        Assert.DoesNotContain(czOnly.Items, i => i.Listing.DesignationId == listing);
        Assert.Equal(1, allowed.Countries.GetValueOrDefault("SK") >= 1 ? 1 : 0);

        var everyone = await new ReviewQueueService(db, TestServices.RelatedParties(db, queries, additionalInfo: false))
            .ListAsync(LinkStatus.Candidate, null, null, ReviewQueueService.MaxItems);
        Assert.DoesNotContain(everyone.Items, i => i.Listing.DesignationId == listing);
        Assert.False(everyone.Systems.ContainsKey(RelationSystems.SkRegister));
    }

    [Fact]
    public async Task AListingTheRegisterFailsForIsSkippedAndStaysPending()
    {
        var failing = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}");
        var fake = PersonRegister();
        fake.FailSurname = "Kravets";

        await using var db = fixture.CreateContext();
        var service = new SkRegisterLookupService(
            fake, db, Options.Create(new SkRegisterOptions { DelayMilliseconds = 0 }), TimeProvider.System, NullLogger<SkRegisterLookupService>.Instance);

        var run = await service.RunAsync(10, [failing], CancellationToken.None);

        Assert.Equal(1, run.Failed);
        Assert.Equal(0, run.Listings);
        Assert.False(await db.ExternalIdentities.AnyAsync(i => i.System == RelationSystems.SkRegister && i.DesignationId == failing));
        Assert.Equal("SUCCESS", (await db.RelationRuns.OrderByDescending(r => r.StartedAt).FirstAsync(r => r.System == RelationSystems.SkRegister)).Status);
    }

    [Fact]
    public async Task ACompanyCarryingTheListedNameIsLinkedAsSharingIt()
    {
        var name = $"Wild{_tag}";
        var listing = await ImportAsync(SubjectType.Organisation, $"Limited Liability Company \"{name} Bank\"");
        var fake = new FakeRegister
        {
            Companies =
            {
                [name.ToLowerInvariant()] =
                [
                    new RpoSearchHit(501, _ico, [$"{name} s. r. o."], null),
                    new RpoSearchHit(502, "11111111", [$"{name}er Slovakia s. r. o."], null),
                ],
            },
            Entities =
            {
                [501] = new RpoCompany(501, _ico, $"{name} s. r. o.", null,
                    [new RpoPerson("Anna Novak", "Anna", "Novak", RelationKind.OwnerOf, "Spoločník", new DateOnly(2019, 8, 6), null, "RU", "Moskva, Ruská federácia")],
                    []),
            },
        };

        var result = await LookupAsync(fake, listing);

        var line = Assert.Single(result.Links);
        Assert.Contains("addresses in RU", line, StringComparison.Ordinal);

        await using var db = fixture.CreateContext();
        var relation = await db.DesignationRelations.Include(r => r.Party).SingleAsync(r => r.Party!.ExternalId == _ico && r.Party.System == RelationSystems.SkRegister);
        Assert.Equal(RelationKind.SharesNameWith, relation.Kind);

        // The shareholder is on no list: the country is kept, the name is not.
        Assert.DoesNotContain("Anna", relation.Note, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheSameSubjectOnAnotherListReusesTheAnswer()
    {
        var first = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}");
        await LookupAsync(PersonRegister(), first);

        var otherSource = "SK_" + Guid.NewGuid().ToString("N")[..8].ToUpperInvariant();
        await using (var db = fixture.CreateContext())
        {
            var source = await db.Sources.FirstAsync(s => s.Code == _sourceCode);
            db.Sources.Add(new SanctionsSource
            {
                Id = Guid.CreateVersion7(),
                JurisdictionId = source.JurisdictionId,
                Code = otherSource,
                Name = otherSource,
                Publisher = "Test",
                LegalEffect = LegalEffect.Binding,
                Format = "JSON",
                LandingPageUrl = "https://example.invalid",
                MinExpectedRecords = 1,
            });
            await db.SaveChangesAsync();
        }

        var second = await ImportAsync(SubjectType.Person, $"Oleg Borisovich KRAVETS {_tag}", otherSource);
        var silent = new FakeRegister();

        var result = await LookupAsync(silent, second);

        Assert.Single(result.Links);
        Assert.Equal(0, silent.Calls);
    }

    private FakeRegister PersonRegister() => new()
    {
        Persons =
        {
            ["Kravets"] =
            [
                new OrsrPersonRow("Oleg Kravets", $"Firma {_tag} s. r. o.", "10", "2"),
                new OrsrPersonRow("Ivan Kravets", "Ina firma s. r. o.", "99", "2"),
            ],
        },
        Icos = { ["10"] = _ico },
        ByIco = { [_ico] = [new RpoSearchHit(401, _ico, [$"Firma {_tag} s. r. o."], null)] },
        Entities =
        {
            [401] = new RpoCompany(401, _ico, $"Firma {_tag} s. r. o.", null,
            [
                new RpoPerson("Oleg Kravets", "Oleg", "Kravets", RelationKind.OwnerOf, "Spoločník v.o.s. / s.r.o.", new DateOnly(2019, 8, 6), null, "RU", "Moskva, Ruská federácia"),
                new RpoPerson("Oleg Ivanovich Kravets", "Oleg", "Kravets", RelationKind.DirectorOf, "Konateľ", new DateOnly(2020, 1, 1), null, "SK", "Bratislava, Slovenská republika"),
            ],
            []),
        },
    };

    private async Task<SkRegisterLookupResult> LookupAsync(FakeRegister fake, Guid listing)
    {
        await using var db = fixture.CreateContext();
        var service = new SkRegisterLookupService(
            fake, db, Options.Create(new SkRegisterOptions { DelayMilliseconds = 0 }), TimeProvider.System, NullLogger<SkRegisterLookupService>.Instance);
        return await service.LookupAsync(listing, null, CancellationToken.None);
    }

    private async Task<Guid> ImportAsync(SubjectType type, string name, string? sourceCode = null)
    {
        var code = sourceCode ?? _sourceCode;
        await using var db = fixture.CreateContext();
        var record = new ParsedDesignation
        {
            SourceRecordKey = "a",
            ExternalId = "a",
            SubjectType = type,
            Names = [new ParsedName { Value = name, Kind = NameType.Primary }],
            Nationalities = type == SubjectType.Person ? [new ParsedNationality { CountryOriginal = "Russia", CountryIso2 = "RU" }] : [],
            RawRecordJson = System.Text.Json.JsonSerializer.Serialize(new { key = "a", name }),
        };

        var outcome = await TestServices
            .Importer(fixture, db, new FakeConnector(code) { Records = [record] }, TestServices.Options(_snapshotRoot))
            .RunAsync(code, force: true, CancellationToken.None);
        Assert.Equal(ImportStatus.Success, outcome.Status);

        return await db.Designations.Where(d => d.Source!.Code == code && d.ValidTo == null).Select(d => d.Id).SingleAsync();
    }

    private sealed class FakeRegister : ISkRegisterClient
    {
        public Dictionary<string, List<OrsrPersonRow>> Persons { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<string, string> Icos { get; } = [];
        public Dictionary<string, List<RpoSearchHit>> ByIco { get; } = [];
        public Dictionary<string, List<RpoSearchHit>> Companies { get; } = new(StringComparer.OrdinalIgnoreCase);
        public Dictionary<long, RpoCompany> Entities { get; } = [];
        public List<string> Extracts { get; } = [];
        public int Calls { get; private set; }
        public string? FailSurname { get; set; }

        public Task<(IReadOnlyList<OrsrPersonRow> Rows, int Total)> SearchPersonAsync(string surname, string? givenName, int page, CancellationToken ct = default)
        {
            Calls++;
            if (string.Equals(surname, FailSurname, StringComparison.OrdinalIgnoreCase))
            {
                throw new HttpRequestException("The register did not answer.");
            }

            var rows = Persons.GetValueOrDefault(surname) ?? [];
            return Task.FromResult<(IReadOnlyList<OrsrPersonRow>, int)>((page == 1 ? rows : [], rows.Count));
        }

        public Task<string?> GetIcoAsync(string id, string sid, CancellationToken ct = default)
        {
            Calls++;
            Extracts.Add(id);
            return Task.FromResult(Icos.GetValueOrDefault(id));
        }

        public Task<IReadOnlyList<RpoSearchHit>> SearchCompaniesAsync(string name, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RpoSearchHit>>(Companies.GetValueOrDefault(name) ?? []);
        }

        public Task<IReadOnlyList<RpoSearchHit>> FindByIcoAsync(string ico, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult<IReadOnlyList<RpoSearchHit>>(ByIco.GetValueOrDefault(ico) ?? []);
        }

        public Task<RpoCompany?> GetCompanyAsync(long id, CancellationToken ct = default)
        {
            Calls++;
            return Task.FromResult(Entities.GetValueOrDefault(id));
        }
    }
}
