using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Connectors.UkFcdo.Tests;

/// <summary>
/// Against twelve designations cut from the published XML. The schema types every
/// field as a string, so what this parser can get wrong is a convention of the
/// publisher's — which numbered part is the surname, what "dd/mm/1963" means, which
/// of two spellings of "Primary name" to accept — rather than the structure.
/// </summary>
public class UkFcdoXmlParserTests
{
    private const string Fixture = "fixtures/uk-fcdo/sample.xml";

    private static async Task<IReadOnlyList<ParsedDesignation>> ParseAsync()
    {
        await using var file = File.OpenRead(Fixture);
        var parsed = new List<ParsedDesignation>();

        await foreach (var designation in UkFcdoXmlParser.ParseAsync(file, CancellationToken.None))
        {
            parsed.Add(designation);
        }

        return parsed;
    }

    private static async Task<ParsedDesignation> SubjectAsync(string uniqueId) =>
        (await ParseAsync()).Single(d => d.ExternalId == uniqueId);

    [Fact]
    public async Task EveryDesignationIsReadUnderItsUniqueId()
    {
        var parsed = await ParseAsync();

        Assert.Equal(12, parsed.Count);
        Assert.Equal(parsed.Count, parsed.Select(d => d.SourceRecordKey).Distinct().Count());
        Assert.All(parsed, d => Assert.Equal(d.ExternalId, d.SourceRecordKey));
        Assert.All(parsed, d => Assert.NotEmpty(d.Names));
        Assert.All(parsed, d => Assert.Single(d.Names, n => n.Kind == NameType.Primary));

        Assert.Equal(8, parsed.Count(d => d.SubjectType == SubjectType.Person));
        Assert.Equal(3, parsed.Count(d => d.SubjectType == SubjectType.Organisation));
        Assert.Equal(1, parsed.Count(d => d.SubjectType == SubjectType.Vessel));
    }

    /// <summary>
    /// Name1–Name5 are given names and Name6 the surname. A "Primary Name Variation" is
    /// another spelling, an "Alias" another name; both are searched, and only the UN's
    /// low-quality mark makes one weak. An empty &lt;Name&gt; (RUS0777 has one) is nothing.
    /// </summary>
    [Fact]
    public async Task NamesAreAssembledFromTheirPartsWithTheSurnameLast()
    {
        var kabir = await SubjectAsync("AFG0007");
        var primary = Assert.Single(kabir.Names, n => n.Kind == NameType.Primary);
        Assert.Equal("Abdul Kabir MOHAMMAD JAN", primary.Value);
        Assert.Equal("Abdul Kabir", primary.GivenName);
        Assert.Equal("MOHAMMAD JAN", primary.FamilyName);
        Assert.Null(primary.MiddleName);

        Assert.Contains(kabir.Names, n => n.Value == "ABDUL KABIR MUHAMMAD JAN" && n.Kind == NameType.Alias && n.MiddleName == "KABIR MUHAMMAD" && n.FamilyName == "JAN");
        Assert.Contains(kabir.Names, n => n.Value == "A Kabir" && n.Kind == NameType.Alias && !n.IsWeak);
        var arabic = Assert.Single(kabir.Names, n => n.Kind == NameType.NativeScript);
        Assert.Equal("عبد الکبیر محمد جان", arabic.Value);

        var anwari = await SubjectAsync("AFG0009");
        var weak = Assert.Single(anwari.Names, n => n.Value == "Mudir");
        Assert.True(weak.IsWeak);
        Assert.Equal(WeakSource.Source, weak.WeakSource);
        Assert.False(Assert.Single(anwari.Names, n => n.Value == "Mohammad Taher Anwari").IsWeak);

        var pumpyanskaya = await SubjectAsync("RUS0777");
        Assert.Equal(3, pumpyanskaya.Names.Count);
        Assert.Equal("Galina Evgenyevna PUMPYANSKAYA", pumpyanskaya.Names[0].Value);
        Assert.All(pumpyanskaya.Names.Where(n => n.Kind == NameType.NativeScript), n => Assert.Equal("ru", n.LanguageCode));

        // "Primary name" in lower case is the primary name too.
        var kratov = await SubjectAsync("GHR0001");
        Assert.Equal("Dmitry Borisovich KRATOV", Assert.Single(kratov.Names, n => n.Kind == NameType.Primary).Value);

        // An entity's whole name is Name6.
        var synesis = await SubjectAsync("BEL0065");
        Assert.Equal("LLC Synesis", synesis.Names[0].Value);
        Assert.Null(synesis.Names[0].FamilyName);
    }

    /// <summary>
    /// "dd/mm/1963" is a year, "dd/09/1958" a month, "1971" a year, "30/01/1972" a day.
    /// Reading the placeholders as zero would put every undated subject on the first
    /// of January, and places are not paired with dates.
    /// </summary>
    [Fact]
    public async Task DatesKeepThePrecisionTheirPlaceholdersGiveThem()
    {
        var kabir = await SubjectAsync("AFG0007");
        var dates = kabir.Births.Where(b => b.Year is not null).ToList();
        Assert.Equal(2, dates.Count);
        Assert.Contains(dates, b => b.Year == 1963 && b.Precision == DatePrecision.Year && b.Month is null);
        Assert.Contains(dates, b => b.Year == 1972 && b.Month == 1 && b.Day == 30 && b.Precision == DatePrecision.Day);

        var place = Assert.Single(kabir.Births, b => b.Year is null);
        Assert.Equal("Nahrin District, Baghlan Province", place.City);
        Assert.Equal("AF", place.CountryIso2);
        Assert.Equal(DatePrecision.Unknown, place.Precision);

        var ibrahim = Assert.Single((await SubjectAsync("AQD0262")).Births, b => b.Year is not null);
        Assert.Equal(DatePrecision.Month, ibrahim.Precision);
        Assert.Equal(((short)1958, (short)9), (ibrahim.Year, ibrahim.Month));
        Assert.Null(ibrahim.Day);

        var motaqi = (await SubjectAsync("AFG0026")).Births.Where(b => b.Year is not null).ToList();
        Assert.Equal(3, motaqi.Count);   // "1971" twice is one
        Assert.Contains(motaqi, b => b.Year == 1971 && b.Precision == DatePrecision.Year);
    }

    /// <summary>
    /// "former USSR (now Belarus)" is Belarus; "Russia" and "Russian Federation" are
    /// both Russia; "DPRK" is North Korea. The wording stays beside the code.
    /// </summary>
    [Fact]
    public async Task CountriesAreReadThroughThePublishersOwnSpellings()
    {
        var karazei = await SubjectAsync("BEL0071");
        var birth = Assert.Single(karazei.Births, b => b.City is not null);
        Assert.Equal("BY", birth.CountryIso2);
        Assert.Equal("former USSR (now Belarus)", birth.CountryOriginal);

        var kratov = await SubjectAsync("GHR0001");
        var nationality = Assert.Single(kratov.Nationalities);
        Assert.Equal(("RU", "Russia"), (nationality.CountryIso2, nationality.CountryOriginal));

        // Two identical nationalities in the file are one.
        Assert.Single((await SubjectAsync("AQD0262")).Nationalities);

        Assert.Equal("KP", UkCountryNames.Find("DPRK")?.Iso2);
        Assert.Equal("CD", UkCountryNames.Find("Congo (Democratic Republic)")?.Iso2);
        Assert.Equal("KZ", UkCountryNames.Find("Kazakh SSR")?.Iso2);
        Assert.Equal("UA", UkCountryNames.Find("Former USSR Currently Ukraine")?.Iso2);
        Assert.Null(UkCountryNames.Find("Former USSR"));
        Assert.Null(UkCountryNames.Find("Kosovo"));
    }

    [Fact]
    public async Task DocumentsKeepTheirProseAndAreTypedFromIt()
    {
        var kabir = await SubjectAsync("AFG0007");
        var passport = Assert.Single(kabir.Identifiers);
        Assert.Equal(IdentifierType.Passport, passport.Kind);
        Assert.Equal("D0009925", passport.ValueNormalized);
        Assert.True(passport.IsDiplomatic);
        Assert.Contains("issued on 22 May 2022", passport.Note);

        // Two passports with different numbers stay two; the same number twice is one.
        Assert.Equal(2, (await SubjectAsync("AFG0026")).Identifiers.Count);

        var ibrahim = await SubjectAsync("AQD0262");
        Assert.Equal(2, ibrahim.Identifiers.Count);
        Assert.All(ibrahim.Identifiers, i => Assert.Equal(IdentifierType.NationalId, i.Kind));

        // A TIN filed as a national identifier is a tax number.
        var pumpyanskaya = await SubjectAsync("RUS0777");
        var tin = Assert.Single(pumpyanskaya.Identifiers);
        Assert.Equal(IdentifierType.TaxId, tin.Kind);
        Assert.Equal("665805050720", tin.ValueNormalized);

        var lobaye = await SubjectAsync("CAF0020");
        var registration = Assert.Single(lobaye.Identifiers);
        Assert.Equal(IdentifierType.CompanyRegistration, registration.Kind);
        Assert.Equal("M 354838 D 0001", registration.ValueOriginal);
    }

    /// <summary>
    /// A ship: "IMO9562233" is the number 9562233, the flag is where the vessel is
    /// registered, and "Prohibition of port entry" is a measure the indicators do type.
    /// </summary>
    [Fact]
    public async Task ShipsCarryTheirImoNumberAndFlag()
    {
        var petrel = await SubjectAsync("DPR0075");

        Assert.Equal(SubjectType.Vessel, petrel.SubjectType);
        Assert.Equal("Petrel 8", petrel.Names[0].Value);
        Assert.Contains(petrel.Names, n => n.Value == "Tian Tai 36");

        var imo = Assert.Single(petrel.Identifiers);
        Assert.Equal(IdentifierType.Imo, imo.Kind);
        Assert.Equal("IMO9562233", imo.ValueOriginal);
        Assert.Equal("9562233", imo.ValueNormalized);

        var flag = Assert.Single(petrel.Addresses);
        Assert.Equal("KM", flag.CountryIso2);
        Assert.Equal("Comoros", flag.CountryOriginal);
        Assert.True(flag.IsPartial);

        Assert.Equal([MeasureType.ServicesRestriction], petrel.Measures);
        Assert.Equal(["PRK"], petrel.Programmes);
    }

    [Fact]
    public async Task AddressesSeparateStreetLinesFromTownAndContactDetails()
    {
        var synesis = await SubjectAsync("BEL0065");

        var minsk = synesis.Addresses.Single(a => a.City == "Minsk");
        Assert.Equal("20B Platonova street", minsk.Street);
        Assert.Equal("BY", minsk.CountryIso2);
        Assert.False(minsk.IsPartial);

        var moscow = synesis.Addresses.Single(a => a.City == "Moscow");
        Assert.Equal("123100", moscow.PostalCode);
        Assert.Equal("RU", moscow.CountryIso2);

        var contact = Assert.Single(synesis.Addresses, a => a.ContactInfo is not null);
        Assert.Contains("S@synesis.y", contact.ContactInfo);
        Assert.Contains("https://synesis.partners", contact.ContactInfo);
        Assert.True(contact.IsPartial);

        // A country alone is a partial address.
        var kabir = await SubjectAsync("AFG0007");
        var countryOnly = Assert.Single(kabir.Addresses);
        Assert.True(countryOnly.IsPartial);
        Assert.Equal("AF", countryOnly.CountryIso2);

        // Five street lines and a town.
        var ibrahim = (await SubjectAsync("AQD0262")).Addresses.Single(a => a.PostalCode == "57572");
        Assert.Equal("Jawa Tengah", ibrahim.City);
        Assert.StartsWith("Masjid Baitul Amin, Waringinrejo RT 01 RW 02", ibrahim.Street);
    }

    /// <summary>
    /// The indicators type the measures; the SanctionsImposed text, which also names
    /// what the indicators have no flag for, stays readable in Remarks.
    /// </summary>
    [Fact]
    public async Task MeasuresComeFromTheIndicators()
    {
        var pumpyanskaya = await SubjectAsync("RUS0777");
        Assert.Equal(
            [MeasureType.AssetFreeze, MeasureType.FundsProhibition, MeasureType.TravelBan, MeasureType.ServicesRestriction, MeasureType.Other],
            pumpyanskaya.Measures);
        Assert.Contains("Sanctions imposed: Asset freeze|Travel Ban|Trust Services Sanctions", pumpyanskaya.Remarks);

        var ibrahim = await SubjectAsync("AQD0262");
        Assert.Contains(MeasureType.TradeRestriction, ibrahim.Measures);

        var exchange = await SubjectAsync("AFG0001");
        Assert.Equal([MeasureType.AssetFreeze, MeasureType.FundsProhibition], exchange.Measures);
    }

    /// <summary>
    /// The regime is the UniqueID's prefix, expressed as the EU's code where the EU has
    /// the same regime, so UK and EU listings under it sit together.
    /// </summary>
    [Fact]
    public async Task ProgrammesUseTheEuCodeWhereOneExists()
    {
        Assert.Equal(["UKR"], (await SubjectAsync("RUS0777")).Programmes);
        Assert.Equal(["TAQA"], (await SubjectAsync("AQD0262")).Programmes);
        Assert.Equal(["HR"], (await SubjectAsync("GHR0001")).Programmes);
        Assert.Equal(["BLR"], (await SubjectAsync("BEL0071")).Programmes);
        Assert.Equal(["AFG"], (await SubjectAsync("AFG0007")).Programmes);
        Assert.Equal(["GUB"], (await SubjectAsync("GUB0009")).Programmes);
    }

    /// <summary>
    /// 1 048 designations transpose a UN listing and name it: the UN's reference is the
    /// UN list's own id here, a publisher-declared link.
    /// </summary>
    [Fact]
    public async Task UnTranspositionsLinkToTheUnListing()
    {
        var injai = await SubjectAsync("GUB0009");
        var link = Assert.Single(injai.CrossReferences);
        Assert.Equal(("UN_SC", "GBi.005"), (link.TargetSourceCode, link.TargetExternalId));
        Assert.Contains("Designation source: UK|UN", injai.Remarks);

        Assert.Equal("TAe.010", Assert.Single((await SubjectAsync("AFG0001")).CrossReferences).TargetExternalId);
        Assert.Empty((await SubjectAsync("RUS0777")).CrossReferences);
    }

    [Fact]
    public async Task LegalBasisIsTheActAndTheRegimeRegulations()
    {
        var parsed = await ParseAsync();
        Assert.All(parsed, d => Assert.Contains(d.LegalBasis, l => l.InstrumentType == "act" && l.InstrumentNumber == "2018 c. 13"));

        var kratov = parsed.Single(d => d.ExternalId == "GHR0001");
        var regulations = Assert.Single(kratov.LegalBasis, l => l.InstrumentType == "regulations");
        Assert.Equal("The Global Human Rights Sanctions Regulations 2020", regulations.Title);
        Assert.Equal(new DateOnly(2020, 7, 6), kratov.ListedAt);
    }

    [Fact]
    public async Task RolesTitlesReasonsAndTheRawRecordSurvive()
    {
        var kabir = await SubjectAsync("AFG0007");

        Assert.Equal("Maulavi", kabir.Title);
        Assert.StartsWith("Governor of Nangarhar Province", kabir.Function);
        Assert.Contains(" | ", kabir.Function);
        Assert.Equal("6909", kabir.PublisherReference);

        // UN transpositions carry no UK statement of reasons; the UK's own designations do.
        Assert.Null(kabir.Reason);
        Assert.NotNull((await SubjectAsync("RUS0777")).Reason);
        Assert.Contains("\"UniqueID\":\"AFG0007\"", kabir.RawRecordJson);
        Assert.Contains("\"DOB\":[\"dd/mm/1963\"", kabir.RawRecordJson);
        Assert.Contains("\"Name6\":\"MOHAMMAD JAN\"", kabir.RawRecordJson);

        var lobaye = await SubjectAsync("CAF0020");
        Assert.Contains("Sanctions imposed: Asset freeze|Director Disqualification Sanction", lobaye.Remarks);
        Assert.Contains("Designation source: UK", lobaye.Remarks);
    }
}
