using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Connectors.NlBz.Tests;

/// <summary>
/// Against the whole published spreadsheet of 2026-09-11. A sheet exported from Excel
/// is a title block, a header, typed date cells and rows that run to column 16 384;
/// what this parser can get wrong is taking the title for an entry or a date's text
/// for its value.
/// </summary>
public class NlBzOdsParserTests
{
    private const string Fixture = "fixtures/nl-bz/sample.ods";

    private static async Task<IReadOnlyList<ParsedDesignation>> ParseAsync()
    {
        await using var file = File.OpenRead(Fixture);
        var parsed = new List<ParsedDesignation>();

        await foreach (var designation in NlBzOdsParser.ParseAsync(file))
        {
            parsed.Add(designation);
        }

        return parsed;
    }

    private static async Task<ParsedDesignation> SubjectAsync(string primaryName) =>
        (await ParseAsync()).Single(d => d.Names[0].Value == primaryName);

    [Fact]
    public async Task EveryEntryIsReadAndTheTitleBlockIsNot()
    {
        var parsed = await ParseAsync();

        Assert.Equal(117, parsed.Count);
        Assert.Equal(114, parsed.Count(d => d.SubjectType == SubjectType.Person));
        Assert.Equal(3, parsed.Count(d => d.SubjectType == SubjectType.Organisation));
        Assert.Equal(parsed.Count, parsed.Select(d => d.SourceRecordKey).Distinct().Count());
        Assert.DoesNotContain(parsed, d => d.Names[0].Value.Contains("Personen en organisaties", StringComparison.Ordinal));
        Assert.All(parsed, d => Assert.Equal(DesignationStatus.Active, d.Status));
    }

    [Fact]
    public async Task PersonsAreGivenNameAndSurnameWithTheAliasBeside()
    {
        var boutahar = await SubjectAsync("Mohammed Amine Boutahar");

        Assert.Equal("Boutahar", boutahar.Names[0].FamilyName);
        Assert.Equal("Mohammed Amine", boutahar.Names[0].GivenName);
        var alias = Assert.Single(boutahar.Names, n => n.Kind == NameType.Alias);
        Assert.Equal("Abu Ubaida al Maghribi", alias.Value);

        Assert.Single((await SubjectAsync("Kawthar Abdellaoui")).Names);
    }

    /// <summary>
    /// Dates are typed cells, read from office:date-value; "10-10-1998" is the display
    /// text and would be ambiguous as text in half the months of the year.
    /// </summary>
    [Fact]
    public async Task DatesComeFromTheTypedCellValue()
    {
        var abdellaoui = await SubjectAsync("Kawthar Abdellaoui");

        var birth = Assert.Single(abdellaoui.Births);
        Assert.Equal(DatePrecision.Day, birth.Precision);
        Assert.Equal(((short)1998, (short)10, (short)10), (birth.Year, birth.Month, birth.Day));
        Assert.Equal(new DateOnly(2016, 12, 7), abdellaoui.ListedAt);
        Assert.Equal("PERSON:kawthar abdellaoui:2016-12-07", abdellaoui.SourceRecordKey);
    }

    /// <summary>
    /// "Rabat (Marokko)" names its country; "Lichtenvoorde" is Dutch by the sheet's own
    /// convention, which brackets every foreign place and none of the Dutch ones.
    /// </summary>
    [Fact]
    public async Task BirthplacesYieldACountryWhetherWrittenOrImplied()
    {
        var boutahar = Assert.Single((await SubjectAsync("Mohammed Amine Boutahar")).Births);
        Assert.Equal("Rabat", boutahar.PlaceOriginal);
        Assert.Equal("Marokko", boutahar.CountryOriginal);
        Assert.Equal("MA", boutahar.CountryIso2);

        var abdellaoui = Assert.Single((await SubjectAsync("Kawthar Abdellaoui")).Births);
        Assert.Equal("Lichtenvoorde", abdellaoui.PlaceOriginal);
        Assert.Null(abdellaoui.CountryOriginal);
        Assert.Equal("NL", abdellaoui.CountryIso2);

        // "(voormalig Joegoslavië, huidig Servië)" — the country it is today.
        var parsed = await ParseAsync();
        var novi = parsed.Single(d => d.Births.Any(b => b.PlaceOriginal == "Novi Pazar"));
        Assert.Equal("RS", novi.Births[0].CountryIso2);
        Assert.Equal("voormalig Joegoslavië, huidig Servië", novi.Births[0].CountryOriginal);
    }

    /// <summary>
    /// Organisations have no given name, and the sheet writes their founding place in
    /// the birthplace column: "n.v.t: opgericht te Rotterdam".
    /// </summary>
    [Fact]
    public async Task OrganisationsKeepTheirFoundingPlaceAsASeat()
    {
        var ragder = await SubjectAsync("RAGDER: Stichting voor gezinnen en jongeren te Rotterdam");
        Assert.Equal(SubjectType.Organisation, ragder.SubjectType);
        Assert.Empty(ragder.Births);
        var seat = Assert.Single(ragder.Addresses);
        Assert.Equal("Rotterdam", seat.City);
        Assert.Equal("NL", seat.CountryIso2);
        Assert.True(seat.IsPartial);

        var theBase = await SubjectAsync("The Base");
        Assert.Equal("US", Assert.Single(theBase.Addresses).CountryIso2);
        Assert.Null(theBase.Addresses[0].City);
    }

    /// <summary>
    /// The reference is written four ways in the sheet. Since 2009 the number alone,
    /// with the year of the decision, addresses the notice; "Stcrt 2007, 39" is a page
    /// in a printed issue and gets no address.
    /// </summary>
    [Fact]
    public async Task TheStaatscourantNoticeIsCitedAndAddressedWhereItCanBe()
    {
        var parsed = await ParseAsync();
        Assert.All(parsed, d => Assert.Contains(d.LegalBasis, l => l.InstrumentNumber == "Sanctieregeling terrorisme 2007-II"));

        var abdellaoui = parsed.Single(d => d.Names[0].Value == "Kawthar Abdellaoui");
        var notice = Assert.Single(abdellaoui.LegalBasis, l => l.InstrumentType == "besluit");
        Assert.Equal("Stcrt. 2016, 68135", notice.InstrumentNumber);
        Assert.Equal("https://zoek.officielebekendmakingen.nl/stcrt-2016-68135.html", notice.OfficialUrl);

        // "Stcrt 2015, 26627" — the year written in.
        var boutahar = parsed.Single(d => d.Names[0].Value == "Mohammed Amine Boutahar");
        Assert.Equal("Stcrt. 2015, 26627", Assert.Single(boutahar.LegalBasis, l => l.InstrumentType == "besluit").InstrumentNumber);

        // "Strct 12368" — misspelt, still a reference.
        var meki = parsed.Single(d => d.Names[0].Value == "Dadi Meki");
        Assert.Equal("Stcrt. 2017, 12368", Assert.Single(meki.LegalBasis, l => l.InstrumentType == "besluit").InstrumentNumber);

        // "Stcrt 2007, 39" — before the numbering, no address.
        var ofkeli = parsed.Single(d => d.Names[0].Value == "Murat Öfkeli");
        Assert.Null(Assert.Single(ofkeli.LegalBasis, l => l.InstrumentType == "besluit").OfficialUrl);
    }

    [Fact]
    public async Task EveryEntryIsAFreezeUnderTheTerrorismProgramme()
    {
        var parsed = await ParseAsync();

        Assert.All(parsed, d => Assert.Equal([MeasureType.AssetFreeze, MeasureType.FundsProhibition], d.Measures));
        Assert.All(parsed, d => Assert.Equal(["TERR"], d.Programmes));
    }

    [Fact]
    public async Task EveryEntryCarriesItsRowVerbatim()
    {
        var boutahar = await SubjectAsync("Mohammed Amine Boutahar");

        Assert.Contains("\"Surname\":\"Boutahar\"", boutahar.RawRecordJson);
        Assert.Contains("\"Publication\":\"Stcrt 2015, 26627\"", boutahar.RawRecordJson);
        Assert.Contains("\"BirthDateValue\":\"1983-04-04", boutahar.RawRecordJson);
    }

    [Fact]
    public async Task AZipWithoutContentIsRefused()
    {
        await using var stream = new MemoryStream();
        using (var zip = new System.IO.Compression.ZipArchive(stream, System.IO.Compression.ZipArchiveMode.Create, leaveOpen: true))
        {
            zip.CreateEntry("mimetype");
        }

        stream.Position = 0;
        var failure = await Assert.ThrowsAsync<NlBzParseException>(async () =>
        {
            await foreach (var _ in NlBzOdsParser.ParseAsync(stream))
            {
            }
        });

        Assert.Contains("content.xml", failure.Message, StringComparison.Ordinal);
    }
}
