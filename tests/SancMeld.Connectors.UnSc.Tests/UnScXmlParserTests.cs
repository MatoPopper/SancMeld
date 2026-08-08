using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Connectors.UnSc.Tests;

/// <summary>
/// Real subjects cut from the published list, chosen for what they exercise. The
/// assertions state what the UN actually published on 2026-08-08, so a change in the
/// export surfaces here rather than in a quietly wrong database.
/// </summary>
public class UnScXmlParserTests : IAsyncLifetime
{
    private const string Fixture = "fixtures/un-sc/sample.xml";

    private IReadOnlyList<ParsedDesignation> _subjects = [];

    public async Task InitializeAsync()
    {
        await using var file = File.OpenRead(Fixture);

        var parsed = new List<ParsedDesignation>();
        await foreach (var designation in UnScXmlParser.ParseAsync(file, CancellationToken.None))
        {
            parsed.Add(designation);
        }

        _subjects = parsed;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ParsedDesignation ByReference(string reference) =>
        _subjects.Single(d => d.ExternalId == reference);

    [Fact]
    public void ParsesBothIndividualsAndEntities()
    {
        Assert.Equal(6, _subjects.Count);
        Assert.Equal(4, _subjects.Count(d => d.SubjectType == SubjectType.Person));
        Assert.Equal(2, _subjects.Count(d => d.SubjectType == SubjectType.Organisation));
        Assert.All(_subjects, d => Assert.NotEmpty(d.Names));
    }

    [Fact]
    public void ExternalIdIsTheReferenceNumberSoTheEuListCanResolveIt()
    {
        // The EU list points at UN subjects by REFERENCE_NUMBER ("LYi.006"), not by
        // DATAID. Storing the wrong one would leave 83 cross-references pointing nowhere.
        var dorda = ByReference("LYi.006");

        Assert.Equal("ABU ZAYD UMAR DORDA", dorda.Names[0].Value);
        Assert.NotEqual(dorda.ExternalId, dorda.SourceRecordKey);
        Assert.All(_subjects, d => Assert.Matches(@"^[A-Z]{2,4}[ie]\.\d+$", d.ExternalId!));
    }

    [Fact]
    public void LowQualityAliasOfAPersonIsWeakAndSaysTheSourceSaidSo()
    {
        // QUALITY = Low is the OFAC sense of a weak alias: a generic spelling that should
        // corroborate a hit rather than raise one. The UN is the first source that marks
        // them — the EU list has no such field at all.
        var weak = _subjects
            .Where(d => d.SubjectType == SubjectType.Person)
            .SelectMany(d => d.Names)
            .Where(n => n.IsWeak)
            .ToList();

        Assert.NotEmpty(weak);
        Assert.All(weak, n => Assert.Equal(WeakSource.Source, n.WeakSource));
    }

    [Fact]
    public void QualityOnAnEntityIsAnAliasTypeAndMustNotBeReadAsWeakness()
    {
        // The same element means something else here: "a.k.a." and "f.k.a." are alias
        // types, not quality judgements. Reading them as quality would mark 603
        // organisation names weak on no evidence whatsoever.
        var organisations = _subjects.Where(d => d.SubjectType == SubjectType.Organisation).ToList();

        Assert.NotEmpty(organisations);
        Assert.All(organisations.SelectMany(d => d.Names), n =>
        {
            Assert.False(n.IsWeak);
            Assert.Equal(WeakSource.None, n.WeakSource);
        });

        // f.k.a. is kept as a distinction: a name no longer used still matches, but it
        // says something different about the present.
        Assert.Contains(organisations.SelectMany(d => d.Names), n => n.Kind == NameType.Former);
    }

    [Fact]
    public void TypeOfDateDecidesWhichDateFieldToBelieve()
    {
        // EXACT reads DATE, APPROXIMATELY means circa, BETWEEN means FROM_YEAR..TO_YEAR.
        // Reading the wrong one turns "born some time in the 1960s" into a precise date.
        var ranged = _subjects
            .SelectMany(d => d.Births)
            .Where(b => b.Precision == DatePrecision.Range)
            .ToList();

        Assert.NotEmpty(ranged);
        Assert.All(ranged, b =>
        {
            Assert.NotNull(b.YearFrom);
            Assert.NotNull(b.YearTo);
            Assert.True(b.YearFrom <= b.YearTo);
        });
    }

    [Fact]
    public void DatesAndPlacesOfBirthAreNotPairedUp()
    {
        // The two are separate collections and do not correspond — one subject on the
        // real list carries ten dates and a single place. Pairing them by position would
        // invent facts, so a date-only and a place-only entry are both legitimate.
        var births = _subjects.SelectMany(d => d.Births).ToList();

        Assert.Contains(births, b => b.Year is not null && b.City is null);
        Assert.Contains(births, b => b.Year is null && (b.City is not null || b.CountryOriginal is not null));
    }

    [Fact]
    public void StatedRoleIsKept()
    {
        // DESIGNATION is a wrapper of one or more VALUE elements — "FDLR Interim
        // President; FDLR-FOCA 1st Vice-President". Same field the EU connector fills.
        Assert.Contains(_subjects, d => d.Function is not null);
    }

    [Fact]
    public void ProgrammeIsTheCommittee()
    {
        // UN_LIST_TYPE names the sanctions committee: Al-Qaida, DPRK, Taliban, DRC…
        Assert.All(_subjects, d => Assert.Single(d.Programmes));
        Assert.Contains(_subjects, d => d.Programmes[0] == "Libya");
    }

    [Fact]
    public void CountriesStayAsTheUnWroteThemRatherThanBecomingFalseIsoCodes()
    {
        // The UN writes countries as free text ("Libyan Arab Jamahiriya"), never as ISO
        // codes. Guessing a code from the text would be inventing data.
        Assert.All(
            _subjects.SelectMany(d => d.Addresses),
            a => Assert.Null(a.CountryIso2));

        Assert.All(
            _subjects.SelectMany(d => d.Nationalities),
            n => Assert.Null(n.CountryIso2));
    }

    [Fact]
    public void EveryListingCarriesTheAssetFreezeAndNoLegalBasis()
    {
        // The consolidated list states no acts of its own — the resolutions behind it are
        // named in the comments, not in a structured field.
        Assert.All(_subjects, d => Assert.Equal([MeasureType.AssetFreeze], d.Measures));
        Assert.All(_subjects, d => Assert.Empty(d.LegalBasis));
    }

    [Fact]
    public async Task SubjectWithoutADataIdFailsTheImportInsteadOfBeingSkipped()
    {
        const string missingId = """
            <CONSOLIDATED_LIST dateGenerated="2026-08-08T23:00:00.000Z">
              <INDIVIDUALS>
                <INDIVIDUAL>
                  <FIRST_NAME>Nobody</FIRST_NAME>
                </INDIVIDUAL>
              </INDIVIDUALS>
            </CONSOLIDATED_LIST>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(missingId));

        await Assert.ThrowsAsync<UnScParseException>(async () =>
        {
            await foreach (var _ in UnScXmlParser.ParseAsync(stream, CancellationToken.None))
            {
            }
        });
    }
}
