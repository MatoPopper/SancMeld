using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.NlBz;

/// <summary>
/// Turns one row of the sheet into a listing.
///
/// The publisher assigns no identifier. The record key is derived — person or
/// organisation, normalised name, date of the decision — and documented here as
/// FingerprintVersion 1. The Staatscourant number would have served, but it is written
/// four different ways ("Stcrt 68135", "Stcrt 2015, 26627", "Strct 12368", "Stcrt68135")
/// and a key must not depend on a typist.
/// </summary>
internal static partial class NlRowMapper
{
    private static readonly JsonSerializerOptions RawOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The regulation every entry rests on: Article 2 lets the minister designate a
    /// person or organisation connected with terrorism within the meaning of UN
    /// Security Council Resolution 1373, and freezes everything they own.
    /// </summary>
    private static readonly ParsedLegalInstrument Regulation = new()
    {
        Relation = "LISTED_BY",
        InstrumentType = "ministeriële regeling",
        InstrumentNumber = "Sanctieregeling terrorisme 2007-II",
        Title = "Sanctieregeling terrorisme 2007-II, krachtens de Sanctiewet 1977",
        OfficialUrl = NlBzLinks.Regulation,
    };

    public static ParsedDesignation Map(NlRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        // Organisations have no given name; the sheet writes their founding place where
        // a person's birthplace goes ("n.v.t: opgericht te Rotterdam").
        var isPerson = row.GivenNames.Length > 0;
        var names = Names(row, isPerson);
        var decided = Date(row.DecisionDateValue, row.DecisionDate);

        return new ParsedDesignation
        {
            ExternalId = null,
            SourceRecordKey = string.Create(CultureInfo.InvariantCulture,
                $"{(isPerson ? "PERSON" : "ENTITY")}:{NameNormalizer.Normalize(names[0].Value).Trim()}:{decided?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}"),
            FingerprintVersion = 1,
            SubjectType = isPerson ? SubjectType.Person : SubjectType.Organisation,
            Status = DesignationStatus.Active,
            ListedAt = decided,
            Names = names,
            Births = isPerson ? Births(row) : [],
            Addresses = isPerson ? [] : Seat(row.BirthPlace),

            // The one measure the regulation has. Listed under the EU's own code for its
            // terrorism measures, which this list is the national counterpart of.
            Measures = [MeasureType.AssetFreeze, MeasureType.FundsProhibition],
            Programmes = ["TERR"],
            LegalBasis = LegalBasis(row.Publication, decided),
            RawRecordJson = JsonSerializer.Serialize(row, RawOptions),
        };
    }

    private static List<ParsedName> Names(NlRow row, bool isPerson)
    {
        var names = new List<ParsedName>();
        var surname = Collapse(row.Surname);
        var given = Collapse(row.GivenNames);

        if (surname.Length == 0)
        {
            throw new NlBzParseException($"Row {row.RowNumber} has no name.");
        }

        names.Add(isPerson
            ? new ParsedName { Value = given + " " + surname, Kind = NameType.Primary, GivenName = given, FamilyName = surname }
            : new ParsedName { Value = surname, Kind = NameType.Primary });

        // The alias column holds a kunya or a second identity — "Abu Ubaida al Maghribi"
        // — never a second spelling, so it is an alias and not a native-script name.
        var alias = Collapse(row.Alias);
        if (alias.Length > 0 && !string.Equals(alias, names[0].Value, StringComparison.OrdinalIgnoreCase))
        {
            names.Add(new ParsedName { Value = alias, Kind = NameType.Alias });
        }

        return names;
    }

    /// <summary>
    /// Dates are typed cells in the sheet, so the ISO value is read first and the text
    /// ("10-10-1998") only when a cell was typed by hand as text.
    /// </summary>
    private static DateOnly? Date(string? isoValue, string text)
    {
        if (isoValue is { Length: >= 10 } && DateOnly.TryParseExact(isoValue[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var iso))
        {
            return iso;
        }

        return DateOnly.TryParseExact(text.Trim(), ["d-M-yyyy", "d/M/yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>
    /// "Rabat (Marokko)" names the country; "Lichtenvoorde" does not, and in a Dutch
    /// government list a place without a country is in the Netherlands — every foreign
    /// place in the sheet carries its country in brackets, none of the Dutch ones does.
    /// The inference is recorded as the code alone, with no country text invented.
    /// </summary>
    private static List<ParsedBirth> Births(NlRow row)
    {
        var date = Date(row.BirthDateValue, row.BirthDate);
        var (place, country) = PlaceAndCountry(row.BirthPlace);

        if (date is null && place is null)
        {
            return [];
        }

        return
        [
            new ParsedBirth
            {
                Year = (short?)date?.Year,
                Month = (short?)date?.Month,
                Day = (short?)date?.Day,
                Precision = date is null ? DatePrecision.Unknown : DatePrecision.Day,
                PlaceOriginal = place,
                CountryOriginal = country,
                CountryIso2 = country is null
                    ? (place is null ? null : "NL")
                    : NlCountryNames.Find(country)?.Iso2,
            },
        ];
    }

    /// <summary>"n.v.t: opgericht te Rotterdam", "n.v.t.: opgericht in de Verenigde Staten".</summary>
    private static List<ParsedAddress> Seat(string text)
    {
        var place = FoundedPattern().Replace(Collapse(text), string.Empty).Trim();

        if (place.Length == 0)
        {
            return [];
        }

        var country = NlCountryNames.Find(place);

        return
        [
            new ParsedAddress
            {
                City = country is null ? place : null,
                CountryIso2 = country?.Iso2 ?? "NL",
                CountryOriginal = country?.Phrase,
                IsPartial = true,
            },
        ];
    }

    private static (string? Place, string? Country) PlaceAndCountry(string text)
    {
        var place = Collapse(text);

        if (place.Length == 0)
        {
            return (null, null);
        }

        var open = place.LastIndexOf('(');
        if (open > 0 && place.EndsWith(')'))
        {
            return (place[..open].Trim(), place[(open + 1)..^1].Trim());
        }

        return (place, null);
    }

    /// <summary>
    /// The Staatscourant notice of the decision. Since 2009 the Staatscourant numbers
    /// its notices within the year, and the reference ("Stcrt 68135", with the year of
    /// the decision) is enough to address it; older references ("Stcrt 2007, 39") are a
    /// page in a printed issue and have no address of their own.
    /// </summary>
    private static List<ParsedLegalInstrument> LegalBasis(string publication, DateOnly? decided)
    {
        var basis = new List<ParsedLegalInstrument> { Regulation };
        var reference = Collapse(publication);

        if (reference.Length == 0)
        {
            return basis;
        }

        var match = ReferencePattern().Match(reference);
        var year = match.Groups["year"].Success ? int.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture) : decided?.Year;
        var number = match.Groups["number"].Success ? match.Groups["number"].Value : null;

        basis.Add(new ParsedLegalInstrument
        {
            Relation = "LISTED_BY",
            InstrumentType = "besluit",
            InstrumentNumber = year is not null && number is not null ? $"Stcrt. {year}, {number}" : reference,
            Title = reference,
            PublicationDate = null,
            OfficialUrl = year is >= 2009 && number is not null ? NlBzLinks.Staatscourant(year.Value, number) : null,
        });

        return basis;
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"^n\.v\.t\.?\s*:?\s*(?:opgericht|gevestigd)\s+(?:te|in(?: de)?)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FoundedPattern();

    [GeneratedRegex(@"(?:Stcrt|Strct)\.?\s*(?:(?<year>\d{4}),\s*)?(?<number>\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ReferencePattern();
}

internal static class NlBzLinks
{
    /// <summary>The consolidated text of the regulation on wetten.overheid.nl (verified 2026-10-15).</summary>
    public const string Regulation = "https://wetten.overheid.nl/BWBR0023131";

    public static string Staatscourant(int year, string number) =>
        string.Create(CultureInfo.InvariantCulture, $"https://zoek.officielebekendmakingen.nl/stcrt-{year}-{number}.html");
}
