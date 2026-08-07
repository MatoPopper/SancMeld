using System.Text.Json;
using SancMeld.Connectors.EuFsf;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Connectors.EuFsf.Tests;

/// <summary>
/// Every subject in the fixture is a real record cut from the published export, chosen
/// because it exercises something the format does that the schema does not warn about.
/// The assertions state what the published data actually contained on 2026-08-06, so a
/// change in the export shows up here rather than in a quietly wrong database.
/// </summary>
public class EuFsfXmlParserTests : IAsyncLifetime
{
    private const string Fixture = "fixtures/eu-fsf/sample.xml";

    private IReadOnlyList<ParsedDesignation> _subjects = [];

    public async Task InitializeAsync()
    {
        await using var file = File.OpenRead(Fixture);

        var parsed = new List<ParsedDesignation>();
        await foreach (var designation in EuFsfXmlParser.ParseAsync(file, CancellationToken.None))
        {
            parsed.Add(designation);
        }

        _subjects = parsed;
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private ParsedDesignation Subject(string logicalId) =>
        _subjects.Single(d => d.SourceRecordKey == logicalId);

    [Fact]
    public void ParsesEverySubjectInTheDocument()
    {
        Assert.Equal(7, _subjects.Count);
        Assert.All(_subjects, d => Assert.NotEmpty(d.Names));
        Assert.All(_subjects, d => Assert.Equal(d.ExternalId, d.SourceRecordKey));
    }

    [Fact]
    public void FirstSpellingIsPrimaryAndRepeatedSpellingsAreDropped()
    {
        // Subject 54 lists "Muzahim Sa'b Hassan Al-Tikriti" twice, once per regulation
        // language. Keeping both would return the same subject twice for one query.
        var names = Subject("54").Names;

        Assert.Equal(2, names.Count);
        Assert.Equal("Muzahim Sa'b Hassan Al-Tikriti", names[0].Value);
        Assert.Equal(NameType.Primary, names[0].Kind);
        Assert.All(names.Skip(1), n => Assert.Equal(NameType.Alias, n.Kind));
    }

    [Fact]
    public void WeakAliasIsNeverTakenFromTheStrongAttribute()
    {
        // The export carries strong="true" on all 31 053 aliases and "false" on none,
        // so reading it would claim every alias is verified when nothing is.
        Assert.All(_subjects.SelectMany(d => d.Names), n =>
        {
            Assert.False(n.IsWeak);
            Assert.Equal(WeakSource.None, n.WeakSource);
        });
    }

    [Fact]
    public void UnknownCountryPlaceholderBecomesNoCountry()
    {
        // countryIso2Code="00" with countryDescription="UNKNOWN" is a placeholder. Stored
        // as-is it reads as a real country and makes every subject of unknown origin look
        // like it shares an origin with every other.
        var births = Subject("54").Births;

        Assert.NotEmpty(births);
        Assert.All(births, b =>
        {
            Assert.Null(b.CountryIso2);
            Assert.Null(b.CountryOriginal);
            Assert.NotNull(b.City);
        });
    }

    [Fact]
    public void HijriYearIsNeverStoredAsAGregorianYear()
    {
        // The export states the calendar per record and does not convert. 1343 AH is
        // roughly 1924 AD, so a Hijri year stored verbatim would make the birth-year
        // qualifier reject the very subject it describes.
        //
        // Subject 127388 shows why this is not hypothetical: the publisher lists the
        // Hijri year 1343 alongside its own Gregorian equivalents, 1964 and 1965.
        var years = Subject("127388").Births.Select(b => b.Year).ToArray();

        Assert.Contains((short)1964, years);
        Assert.Contains((short)1965, years);
        Assert.DoesNotContain((short)1343, years);

        Assert.All(_subjects.SelectMany(d => d.Births), b =>
            Assert.DoesNotContain(b.Year, new short?[] { 1343, 1402 }));
    }

    [Fact]
    public void HijriRecordWithoutAGregorianEquivalentKeepsItsPlaceButNoDate()
    {
        // Dropping the record entirely would lose a place of birth the other records
        // do not carry any better; inventing a date would be worse.
        var undated = Assert.Single(Subject("127388").Births, b => b.Year is null);

        Assert.Null(undated.Month);
        Assert.Null(undated.Day);
        Assert.Equal(DatePrecision.Unknown, undated.Precision);
        Assert.Equal("Farsan", undated.City);
    }

    [Fact]
    public void HijriRecordUsesTheGregorianDateWhenTheExportSuppliesOne()
    {
        // Subject 6177 has an Islamic-calendar birth whose parts read 1402-06-24 but
        // whose birthdate attribute is already converted to 1982-04-19. That converted
        // value is usable, so the record is kept rather than blanked.
        var births = Subject("6177").Births;

        Assert.All(births, b => Assert.Equal(DatePrecision.Day, b.Precision));
        Assert.All(births, b => Assert.Equal((short)1982, b.Year));
        Assert.Equal(2, births.Count(b => b is { Month: 4, Day: 19 }));
    }

    [Fact]
    public void YearRangeIsARangeEvenWhenTheRecordIsAlsoMarkedCirca()
    {
        // Subject 521 is circa AND a 1953-1958 range. The range is the vaguer statement,
        // so collapsing it to Circa would throw away the only bounds available.
        var birth = Subject("521").Births[0];

        Assert.Equal(DatePrecision.Range, birth.Precision);
        Assert.Equal((short)1953, birth.YearFrom);
        Assert.Equal((short)1958, birth.YearTo);
        Assert.Null(birth.Year);
    }

    [Fact]
    public void CircaWithoutARangeLowersThePrecision()
    {
        var circa = Subject("54").Births.First(b => b.Year == 1946);

        Assert.Equal(DatePrecision.Circa, circa.Precision);
    }

    [Fact]
    public void EnterpriseIsAnOrganisation()
    {
        Assert.Equal(SubjectType.Organisation, Subject("983").SubjectType);
        Assert.Equal(SubjectType.Person, Subject("54").SubjectType);
    }

    [Fact]
    public void AddressWithOnlyAPlaceStillGetsALocality()
    {
        // 77 addresses in the export give "place" and no "city".
        var address = Subject("983").Addresses.Single(a => a.PostalCode == "60482");

        Assert.Equal("Worth", address.City);
    }

    [Fact]
    public void PoBoxStaysInItsOwnFieldRatherThanBeingGluedOntoTheStreet()
    {
        var khartoum = Subject("983").Addresses.Single(a => a.City == "Khartoum");

        Assert.Equal("P.O. Box 1937", khartoum.PoBox);
        Assert.Null(khartoum.Street);
    }

    [Fact]
    public void CountryOnlyAddressIsMarkedPartial()
    {
        // "Somewhere in Bangladesh" is true of 170 million people; treated as a normal
        // address it would corroborate hits it has no business corroborating.
        var addresses = Subject("983").Addresses;

        Assert.True(addresses.Single(a => a.CountryIso2 == "BD").IsPartial);
        Assert.False(addresses.Single(a => a.City == "Palos Hills").IsPartial);
    }

    [Fact]
    public void PublisherDeclaredUnListingBecomesACrossReference()
    {
        // The strongest deduplication signal in the dataset, and it costs nothing:
        // the publisher states which UN listing this subject implements.
        var xref = Assert.Single(Subject("6106").CrossReferences);

        Assert.Equal("UN_SC", xref.TargetSourceCode);
        Assert.Equal("LYi.006", xref.TargetExternalId);
    }

    [Fact]
    public void SubjectsWithoutAUnListingHaveNoCrossReference() =>
        Assert.Empty(Subject("54").CrossReferences);

    [Fact]
    public void IdentifiersKeepTheirTypeAndAComparableForm()
    {
        var passport = Assert.Single(Subject("6106").Identifiers);

        Assert.Equal(IdentifierType.Passport, passport.Kind);
        Assert.Equal("FK117RK0", passport.ValueOriginal);
        Assert.Equal("LY", passport.IssuingCountry);

        // Validity has its own columns; a passport that expired in 2010 is still the one
        // to match on, but a screening decision should be able to see that it has.
        Assert.Equal(new DateOnly(2018, 11, 25), passport.IssuedAt);
        Assert.Equal(new DateOnly(2026, 11, 24), passport.ExpiresAt);

        var expired = Subject("6177").Identifiers.Single(i => i.ValueOriginal == "F654645");
        Assert.True(expired.IsExpired);
        Assert.Equal(new DateOnly(2010, 3, 7), expired.ExpiresAt);

        // "36-3823186" and "363823186" are the same registration number.
        var taxNumber = Assert.Single(Subject("983").Identifiers);
        Assert.Equal("36-3823186", taxNumber.ValueOriginal);
        Assert.Equal("363823186", taxNumber.ValueNormalized);
    }

    [Theory]
    [InlineData("AB-123 456", "AB123456")]
    [InlineData("ab123456", "AB123456")]
    [InlineData("IMO 9876543", "IMO9876543")]
    [InlineData("---", "")]
    public void IdentifierNormalisationIsCaseAndPunctuationBlind(string input, string expected)
        => Assert.Equal(expected, EuFsfXmlParser.NormalizeIdentifier(input));

    [Fact]
    public void MissingDesignationDateIsNotInvented()
    {
        // 580 of 6 234 subjects carry no designation date.
        Assert.Null(Subject("54").ListedAt);
        Assert.Equal(new DateOnly(2011, 2, 26), Subject("6106").ListedAt);
    }

    [Fact]
    public void EveryListingCarriesTheAssetFreezeAndNothingElse()
    {
        // The export has no measure field. The freeze is the list's own scope; any
        // narrower obligation would be a legal claim the data does not make.
        Assert.All(_subjects, d => Assert.Equal([MeasureType.AssetFreeze], d.Measures));
    }

    [Fact]
    public void EveryListingCitesTheActItStandsUnder()
    {
        // All 6 234 subjects carry a regulation, and all of them carry a URL. Without it a
        // hit is an assertion; with it, it is a citation.
        //
        // Not asserting the host: the links point at four different official domains —
        // eur-lex.europa.eu (6 210), data.europa.eu ELI (15), press.un.org (7) and
        // docs.un.org (2) — and 668 of them are http rather than https. The URLs are the
        // publisher's, and rewriting them to look tidier would be falsifying a citation.
        Assert.All(_subjects, d =>
        {
            var act = Assert.Single(d.LegalBasis);

            Assert.NotNull(act.OfficialUrl);
            Assert.True(Uri.TryCreate(act.OfficialUrl, UriKind.Absolute, out var url));
            Assert.Contains(url!.Scheme, new[] { "http", "https" });
            Assert.NotNull(act.InstrumentNumber);
        });
    }

    [Fact]
    public void AmendmentIsRecordedAsAnAmendmentNotAsTheOriginalListing()
    {
        // 6 071 of the 6 234 regulation elements are amendments. The element names the act
        // that last touched the entry, so claiming it listed the subject would be wrong.
        var act = Assert.Single(Subject("54").LegalBasis);

        Assert.Equal("regulation", act.InstrumentType);
        Assert.Equal("LISTED_BY", act.Relation);
        Assert.Equal("1210/2003", act.InstrumentNumber);
        Assert.Equal("1210/2003 (OJ L169)", act.Title);
        Assert.Equal(new DateOnly(2003, 7, 8), act.PublicationDate);
        Assert.Equal(new DateOnly(2003, 7, 7), act.EffectiveDate);
    }

    [Fact]
    public void CelexIsTakenFromTheUrlOnlyWhereTheUrlCarriesOne()
    {
        // Older acts are addressed by CELEX, newer ones by an OJ reference. The identifier
        // is worth extracting where it exists and worth not inventing where it does not.
        var withCelex = _subjects
            .SelectMany(d => d.LegalBasis)
            .Where(a => a.CelexNumber is not null)
            .ToList();

        Assert.All(withCelex, a =>
        {
            Assert.Contains("CELEX", a.OfficialUrl!, StringComparison.OrdinalIgnoreCase);
            Assert.Matches(@"^\d{5}[A-Z]\d{4}$", a.CelexNumber!);
        });

        Assert.All(
            _subjects.SelectMany(d => d.LegalBasis).Where(a => a.CelexNumber is null),
            a => Assert.DoesNotContain("CELEX", a.OfficialUrl ?? string.Empty, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ProgrammeComesFromTheRegulation()
    {
        Assert.Equal(["IRQ"], Subject("54").Programmes);
        Assert.Equal(["AFG"], Subject("521").Programmes);
    }

    [Fact]
    public void RawRecordIsQueryableJsonThatKeepsWhatTheModelDropped()
    {
        using var raw = JsonDocument.Parse(Subject("6106").RawRecordJson);
        var root = raw.RootElement;

        Assert.Equal("6106", root.GetProperty("@logicalId").GetString());
        Assert.Equal("LYi.006", root.GetProperty("@unitedNationId").GetString());

        // Repeated elements become arrays, single ones stay objects, and text-only
        // elements stay strings — otherwise a jsonb query has to guess the shape.
        Assert.Equal(JsonValueKind.Array, root.GetProperty("nameAlias").ValueKind);
        Assert.Equal(JsonValueKind.Object, root.GetProperty("subjectType").ValueKind);

        // Fields the model has no column for are still recoverable: the passport's
        // expiry is only ever in here.
        var identification = root.GetProperty("identification");
        Assert.Equal("2026-11-24", identification.GetProperty("@validTo").GetString());
    }

    [Fact]
    public async Task MalformedSubjectFailsTheImportInsteadOfBeingSkipped()
    {
        const string missingId = """
            <export xmlns="http://eu.europa.ec/fpi/fsd/export" globalFileId="1">
              <sanctionEntity euReferenceNumber="EU.1.1">
                <subjectType code="person" classificationCode="P"/>
              </sanctionEntity>
            </export>
            """;

        using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(missingId));

        await Assert.ThrowsAsync<EuFsfParseException>(async () =>
        {
            await foreach (var _ in EuFsfXmlParser.ParseAsync(stream, CancellationToken.None))
            {
                // Draining the sequence is the point: the exception must surface.
            }
        });
    }
}
