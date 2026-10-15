using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Connectors.PlMswia.Tests;

/// <summary>
/// Against 27 rows cut from the published workbook. Every cell is prose, so what this
/// parser can get wrong is reading a sentence slightly differently from how the ministry
/// wrote it: a year taken for a day, a bracket taken for a name, a footnote taken for a
/// subject.
/// </summary>
public class PlMswiaXlsxParserTests
{
    private const string Fixture = "fixtures/pl-mswia/sample.xlsx";

    private static async Task<IReadOnlyList<ParsedDesignation>> ParseAsync()
    {
        await using var file = File.OpenRead(Fixture);
        var parsed = new List<ParsedDesignation>();

        await foreach (var designation in PlMswiaXlsxParser.ParseAsync(file))
        {
            parsed.Add(designation);
        }

        return parsed;
    }

    private static async Task<ParsedDesignation> SubjectAsync(string primaryName) =>
        (await ParseAsync()).Single(d => d.Names[0].Value == primaryName);

    [Fact]
    public async Task EveryListedRowIsReadAndTheFootnotesAreNot()
    {
        var parsed = await ParseAsync();

        Assert.Equal(13, parsed.Count(d => d.SubjectType == SubjectType.Person));
        Assert.Equal(14, parsed.Count(d => d.SubjectType == SubjectType.Organisation));
        Assert.All(parsed, d => Assert.NotEmpty(d.Names));
        Assert.DoesNotContain(parsed, d => d.Names[0].Value.StartsWith('['));
        Assert.Equal(parsed.Count, parsed.Select(d => d.SourceRecordKey).Distinct().Count());
    }

    /// <summary>
    /// The family name is the run of capitals the name starts with; the brackets hold the
    /// other spellings. Reading the cell as one name would index "BELOV Alexey (BELOV
    /// Alexy)" as a person nobody will ever type.
    /// </summary>
    [Fact]
    public async Task PersonNamesSplitIntoFamilyGivenAndAliases()
    {
        var belov = await SubjectAsync("BELOV Alexey");
        var primary = Assert.Single(belov.Names, n => n.Kind == NameType.Primary);

        Assert.Equal("BELOV", primary.FamilyName);
        Assert.Equal("Alexey", primary.GivenName);
        var alias = Assert.Single(belov.Names, n => n.Kind == NameType.Alias);
        Assert.Equal("BELOV Alexy", alias.Value);
        Assert.Equal("BELOV", alias.FamilyName);

        // "(w zapisie także „LESZCZUK Władimir”, „ЛEЩУK …” lub „LESHCHUK Vladimir
        // Evgenyevich)" — three spellings, one of them in Cyrillic, one bracket left
        // unclosed by the publisher.
        var liashchuk = await SubjectAsync("LIASHCHUK Uladzimir");
        Assert.Contains(liashchuk.Names, n => n.Value == "LESZCZUK Władimir" && n.Kind == NameType.Alias);
        Assert.Contains(liashchuk.Names, n => n.Value == "LESHCHUK Vladimir Evgenyevich" && n.Kind == NameType.Alias);
        Assert.Contains(liashchuk.Names, n => n.Kind == NameType.NativeScript && n.Value.Contains('Л'));
        Assert.Equal(4, liashchuk.Names.Count);

        var sznejder = await SubjectAsync("SZNEJDER Siergiej Iwanowicz");
        Assert.Equal("SZNEJDER", sznejder.Names[0].FamilyName);
        Assert.Equal("Siergiej Iwanowicz", sznejder.Names[0].GivenName);
        Assert.Equal(2, sznejder.Names.Count(n => n.Kind == NameType.NativeScript));
    }

    /// <summary>
    /// "urodzony w 1966 roku" is a year and "urodzona 18.05.1992 r." a day. Reading the
    /// first as the first of January would contradict every real date for the subject.
    /// </summary>
    [Fact]
    public async Task BirthDatesKeepThePrecisionTheyWereWrittenWith()
    {
        var day = Assert.Single((await SubjectAsync("ALAUDINOV Apti Aronovich")).Births);
        Assert.Equal(DatePrecision.Day, day.Precision);
        Assert.Equal(((short)1973, (short)10, (short)5), (day.Year, day.Month, day.Day));

        var year = Assert.Single((await SubjectAsync("SZNEJDER Siergiej Iwanowicz")).Births);
        Assert.Equal(DatePrecision.Year, year.Precision);
        Assert.Equal((short)1966, year.Year);
        Assert.Null(year.Month);

        var numeric = Assert.Single((await SubjectAsync("IVANISHCHEVA Anzhelika")).Births);
        Assert.Equal(DatePrecision.Day, numeric.Precision);
        Assert.Equal(((short)1992, (short)5, (short)18), (numeric.Year, numeric.Month, numeric.Day));

        // No date at all for ŞOR Ilan — only a nationality.
        Assert.Empty((await SubjectAsync("ŞOR Ilan")).Births);
    }

    [Fact]
    public async Task BirthplacesKeepTheirWordingAndYieldACountry()
    {
        var belov = Assert.Single((await SubjectAsync("BELOV Alexey")).Births);
        Assert.Equal("Wilnie", belov.PlaceOriginal);
        Assert.Equal("Litwa", belov.CountryOriginal);
        Assert.Equal("LT", belov.CountryIso2);

        var liashchuk = Assert.Single((await SubjectAsync("LIASHCHUK Uladzimir")).Births);
        Assert.Equal("Łuniniec (Luniniec)", liashchuk.PlaceOriginal);
        Assert.Equal("BY", liashchuk.CountryIso2);

        // The place on the line after the date.
        var petrov = Assert.Single((await SubjectAsync("PETROV Igor")).Births);
        Assert.Equal("Kaliningradzie", petrov.PlaceOriginal);
        Assert.Equal("RU", petrov.CountryIso2);
        Assert.Equal(DatePrecision.Day, petrov.Precision);
    }

    [Fact]
    public async Task NationalitiesAreReadInEveryInflectionAndSplitOnConjunctions()
    {
        var belov = Assert.Single((await SubjectAsync("BELOV Alexey")).Nationalities);
        Assert.Equal("RU", belov.CountryIso2);
        Assert.Equal("rosyjski", belov.CountryOriginal);

        var sor = (await SubjectAsync("ŞOR Ilan")).Nationalities;
        Assert.Equal(["MD", "IL"], sor.Select(n => n.CountryIso2));

        var karpovich = (await SubjectAsync("KARPOVICH Andrei")).Nationalities;
        Assert.Equal(["BY", "RU"], karpovich.Select(n => n.CountryIso2));
        Assert.Equal("Republiki Białorusi", karpovich[0].CountryOriginal);
    }

    [Fact]
    public async Task PersonalNumbersBecomeIdentifiers()
    {
        var pesel = Assert.Single((await SubjectAsync("BELOV Alexey")).Identifiers);
        Assert.Equal(IdentifierType.NationalId, pesel.Kind);
        Assert.Equal("63111513931", pesel.ValueNormalized);
        Assert.Equal("PL", pesel.IssuingCountry);

        var tin = Assert.Single((await SubjectAsync("SZNEJDER Siergiej Iwanowicz")).Identifiers);
        Assert.Equal(IdentifierType.TaxId, tin.Kind);
        Assert.Equal("246000069678", tin.ValueNormalized);

        var harutyunyan = Assert.Single((await SubjectAsync("HARUTYUNYAN Armen Seryozhaevich")).Identifiers);
        Assert.Equal("72100716930", harutyunyan.ValueNormalized);
    }

    /// <summary>
    /// Registry numbers by label, wherever the label sits, and the address is what is
    /// left. A Polish office has no country written on it; its postcode says PL.
    /// </summary>
    [Fact]
    public async Task EntityRegistryNumbersAndAddressesAreSeparated()
    {
        var onec = await SubjectAsync("„1C-POLAND” Sp. z o.o.");
        var krs = Assert.Single(onec.Identifiers, i => i.Kind == IdentifierType.CompanyRegistration);
        Assert.Equal("0000312056", krs.ValueNormalized);
        Assert.Equal("PL", krs.IssuingCountry);
        var nip = Assert.Single(onec.Identifiers, i => i.Kind == IdentifierType.TaxId);
        Assert.Equal("5222894064", nip.ValueNormalized);
        var office = Assert.Single(onec.Addresses);
        Assert.Equal("ul. Barkocińska 6 lok. 23, 03-543 Warszawa", office.Street);
        Assert.Equal("PL", office.CountryIso2);

        // Four Russian numbers on four lines after the address.
        var atm = await SubjectAsync("ATM OBSHESTVO S OGRANICHENNOJ OTVETSTVENNOSTYU – OOO ATM");
        Assert.Equal(
            [("INN", IdentifierType.TaxId), ("KPP", IdentifierType.TaxId), ("OGRN", IdentifierType.CompanyRegistration), ("OKPO", IdentifierType.Other)],
            atm.Identifiers.Select(i => (i.Note, i.Kind)));
        Assert.All(atm.Identifiers, i => Assert.Equal("RU", i.IssuingCountry));
        Assert.Equal("RU", Assert.Single(atm.Addresses).CountryIso2);
        Assert.Equal("Federacja Rosyjska", atm.Addresses[0].CountryOriginal);

        // "Numer identyfikacyjny" on one line, "BIN: 120240019110" on the next.
        var kazakh = await SubjectAsync("SEMIPALATINSK WOOD PROCESSING PLANT No. 1 LLP");
        var bin = Assert.Single(kazakh.Identifiers);
        Assert.Equal("120240019110", bin.ValueNormalized);
        Assert.Equal("KZ", bin.IssuingCountry);

        // A number written in prose, in Greek letters: "pod numerem ΗΕ 309661".
        var rainbee = await SubjectAsync("Rainbee Holdings Limited");
        Assert.Equal("CY", Assert.Single(rainbee.Addresses).CountryIso2);
        Assert.Contains(rainbee.Identifiers, i => i.ValueOriginal == "ΗΕ 309661" && i.Kind == IdentifierType.CompanyRegistration);
    }

    [Fact]
    public async Task ARussianScriptAddressIsASecondAddressOfTheSameOffice()
    {
        var nft = await SubjectAsync("NFT LLC");

        Assert.Equal(2, nft.Addresses.Count);
        Assert.Equal("BY", nft.Addresses[0].CountryIso2);
        Assert.StartsWith("ul. Pobedy 38A", nft.Addresses[0].Street);
        Assert.Contains("Коптевка", nft.Addresses[1].Street);
        Assert.Equal("BY", nft.Addresses[1].CountryIso2);

        var registration = Assert.Single(nft.Identifiers);
        Assert.Equal("591534088", registration.ValueNormalized);
        Assert.Equal(IdentifierType.CompanyRegistration, registration.Kind);

        Assert.Contains(nft.Names, n => n.Kind == NameType.NativeScript && n.Value.Contains("НФТ"));
    }

    /// <summary>
    /// "(obecnie: X)" is what the company is called now and "(do 3.03.2022 r. – Y)" what
    /// it was called before; both are searched, the second as a former name.
    /// </summary>
    [Fact]
    public async Task EntityNamesKeepCurrentFormerAndNativeSpellings()
    {
        var gazprom = await SubjectAsync("Gazprom Export LLC");
        Assert.Contains(gazprom.Names, n => n.Value == "OOO Gazprom Export" && n.Kind == NameType.Alias);

        var medmix = await SubjectAsync("MEDMIX POLAND Sp. z o.o.");
        var former = Assert.Single(medmix.Names, n => n.Kind == NameType.Former);
        Assert.Equal("SULZER MIXPAC POLAND Sp. z o.o.", former.Value);

        var severstal = await SubjectAsync("SIA SEVERSTAL DISTRIBUTION Sp. z o.o. Oddział w Polsce");
        var current = Assert.Single(severstal.Names, n => n.Kind == NameType.Alias);
        Assert.Equal("SIA Marcegaglia Baltics Sp. z o.o. Oddział w Polsce", current.Value);

        var mtz = await SubjectAsync("MTZ BELARUS");
        var native = Assert.Single(mtz.Names, n => n.Kind == NameType.NativeScript);
        Assert.Equal("Минский тракторный завод МТЗ-XOЛДИHГ", native.Value);
    }

    /// <summary>
    /// The list keeps what it has withdrawn, dated. KAMAZ was listed on 26 April 2022 and
    /// removed on 10 June 2022, both as Excel serials in the file.
    /// </summary>
    [Fact]
    public async Task DelistedRowsCarryTheirStatusAndBothDates()
    {
        var kamaz = await SubjectAsync("KAMAZ");
        Assert.Equal(DesignationStatus.Delisted, kamaz.Status);
        Assert.Equal(new DateOnly(2022, 4, 26), kamaz.ListedAt);
        Assert.Equal(new DateOnly(2022, 6, 10), kamaz.DelistedAt);

        var melniczenko = await SubjectAsync("MELNICZENKO Aleksandra");
        Assert.Equal(DesignationStatus.Delisted, melniczenko.Status);
        Assert.NotNull(melniczenko.DelistedAt);

        var alaudinov = await SubjectAsync("ALAUDINOV Apti Aronovich");
        Assert.Equal(DesignationStatus.Active, alaudinov.Status);
        Assert.Equal(new DateOnly(2022, 10, 27), alaudinov.ListedAt);
        Assert.Null(alaudinov.DelistedAt);
    }

    /// <summary>
    /// Measures are per row. Some rows carry only the entry ban, and a freeze on them
    /// would be an obligation the ministry never imposed.
    /// </summary>
    [Fact]
    public async Task MeasuresAreReadPerRowFromTheirWording()
    {
        var alaudinov = await SubjectAsync("ALAUDINOV Apti Aronovich");
        Assert.Equal([MeasureType.AssetFreeze, MeasureType.FundsProhibition, MeasureType.EntryBan], alaudinov.Measures);
        Assert.Equal(["UKR"], alaudinov.Programmes);
        Assert.Contains("zamrożenie", alaudinov.Remarks);

        var nieczajew = await SubjectAsync("NIECZAJEW Aleksiej");
        Assert.Equal([MeasureType.EntryBan], nieczajew.Measures);

        var babkin = await SubjectAsync("BABKIN Konstantin");
        Assert.Contains(MeasureType.Other, babkin.Measures);

        // Footnotes [4] and [5] cite Regulation 765/2006, the Belarus programme.
        var gucerjew = await SubjectAsync("GUCERJEW Sait-Salam");
        Assert.Equal(["BLR"], gucerjew.Programmes);
    }

    /// <summary>
    /// Every listing cites the act; a row with a public link to the decision cites that
    /// too. A link to a file on the editor's own disk is not a citation anyone can
    /// follow, and a row with no link gets the act alone.
    /// </summary>
    [Fact]
    public async Task LegalBasisIsTheActAndThePublishedDecision()
    {
        var parsed = await ParseAsync();
        Assert.All(parsed, d => Assert.Contains(d.LegalBasis, l => l.InstrumentType == "ustawa" && l.InstrumentNumber == "Dz.U. 2022 poz. 835"));

        var alaudinov = parsed.Single(d => d.Names[0].Value == "ALAUDINOV Apti Aronovich");
        var decision = Assert.Single(alaudinov.LegalBasis, l => l.InstrumentType.StartsWith("decyzja"));
        Assert.StartsWith("https://www.gov.pl/attachment/", decision.OfficialUrl);
        Assert.Equal("LISTED_BY", decision.Relation);

        var rostumel = parsed.Single(d => d.Names[0].Value == "ROSTUMEL HOLDING LIMITED");
        Assert.Single(rostumel.LegalBasis);

        var kamaz = parsed.Single(d => d.Names[0].Value == "KAMAZ");
        Assert.Single(kamaz.LegalBasis);
    }

    /// <summary>
    /// The publisher assigns no id, so the key is derived: sheet, normalised name, date
    /// listed. The recipe is what makes a row the same row in next month's file.
    /// </summary>
    [Fact]
    public async Task RecordKeyIsDerivedFromSheetNameAndListingDate()
    {
        var belov = await SubjectAsync("BELOV Alexey");

        Assert.Equal("PERSON:belov alexey:2022-05-13", belov.SourceRecordKey);
        Assert.Null(belov.ExternalId);
        Assert.Equal(1, belov.FingerprintVersion);

        var onec = await SubjectAsync("„1C-POLAND” Sp. z o.o.");
        Assert.StartsWith("ENTITY:1c poland sp z o o:", onec.SourceRecordKey);
    }

    [Fact]
    public async Task EveryRecordCarriesItsRowVerbatim()
    {
        foreach (var designation in await ParseAsync())
        {
            Assert.Contains("\"sheet\":", designation.RawRecordJson);
            Assert.Contains("\"identity\":", designation.RawRecordJson);
        }

        var belov = await SubjectAsync("BELOV Alexey");
        Assert.Contains("urodzony 15 listopada 1963 r. w Wilnie (Litwa)", belov.RawRecordJson);
        Assert.Contains("\"row\":4", belov.RawRecordJson);
        Assert.Equal("Alexey Belov", belov.Reason![..12]);
    }
}
