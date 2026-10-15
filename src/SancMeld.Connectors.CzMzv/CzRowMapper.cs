using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// Turns one entry of the list into a listing.
///
/// The publisher assigns no identifier. The record key is derived — person or entity,
/// normalised primary name, date of entry — and documented here as FingerprintVersion
/// 1. The date is part of it because the list keeps a subject's earlier entries beside
/// the current one, each under its own government resolution.
/// </summary>
internal static partial class CzRowMapper
{
    private static readonly JsonSerializerOptions RawOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static readonly Dictionary<string, int> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ledna"] = 1,
        ["února"] = 2,
        ["března"] = 3,
        ["dubna"] = 4,
        ["května"] = 5,
        ["června"] = 6,
        ["července"] = 7,
        ["srpna"] = 8,
        ["září"] = 9,
        ["října"] = 10,
        ["listopadu"] = 11,
        ["prosince"] = 12,
    };

    /// <summary>
    /// The act every entry rests on. The CSV cites it only in the status notes, so it
    /// is stated once here.
    /// </summary>
    private static readonly ParsedLegalInstrument Act = new()
    {
        Relation = "LISTED_BY",
        InstrumentType = "zákon",
        InstrumentNumber = "1/2023 Sb.",
        Title = "Zákon č. 1/2023 Sb., o omezujících opatřeních proti některým závažným jednáním uplatňovaných " +
                "v mezinárodních vztazích (sankční zákon)",
        OfficialUrl = "https://www.e-sbirka.cz/sb/2023/1",
    };

    /// <summary>
    /// Null for an entry the list itself calls superseded. "Změněn" marks the earlier
    /// wording of an entry that a later resolution replaced — "the current wording is
    /// in the valid entry", the note says. The valid entry is imported; this one is
    /// the publisher's own version history, and this system keeps history of its own.
    /// </summary>
    public static ParsedDesignation? Map(CzRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var status = row.Status.ToLowerInvariant() switch
        {
            "platný" => DesignationStatus.Active,
            "zrušen" => DesignationStatus.Delisted,
            "pozastaven" => DesignationStatus.Suspended,
            "změněn" => (DesignationStatus?)null,
            _ => throw new CzMzvParseException(
                $"Row {row.RowNumber} has entry status '{row.Status}', which this connector does not know. " +
                "Importing it as active or as removed would both be a guess."),
        };

        if (status is null)
        {
            return null;
        }

        var isPerson = row.GivenNames.Length > 0;
        var names = CzNames.Parse(isPerson, row.Surname, row.GivenNames);

        if (names.Count == 0)
        {
            throw new CzMzvParseException($"Row {row.RowNumber} has no name.");
        }

        var listedAt = Date(row.ListedAt);
        var (measures, programmes) = CzMeasures.Parse(row.EuProvision, row.Measures);

        return new ParsedDesignation
        {
            ExternalId = null,
            SourceRecordKey = string.Create(CultureInfo.InvariantCulture,
                $"{(isPerson ? "PERSON" : "ENTITY")}:{NameNormalizer.Normalize(names[0].Value).Trim()}:{listedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}"),
            FingerprintVersion = 1,
            SubjectType = isPerson ? SubjectType.Person : SubjectType.Organisation,
            Status = status.Value,
            ListedAt = listedAt,

            // "Zápis byl zrušen ke dni 24. února 2025 … vzhledem k zařazení subjektu na
            // sankční seznam Evropské unie": the date is in the note, and so is the
            // reason, which stays in Remarks for a reader.
            DelistedAt = status == DesignationStatus.Delisted ? NoteDate(row.StatusNote) : null,
            Reason = Blank(row.Reason),
            Remarks = Remarks(row),
            Names = names,
            Births = Births(row),
            Nationalities = isPerson ? Nationalities(row.Country) : [],
            Addresses = isPerson ? [] : Seat(row.Country),
            Measures = measures,
            Programmes = programmes,
            LegalBasis = LegalBasis(row.Instrument),
            RawRecordJson = JsonSerializer.Serialize(row, RawOptions),
        };
    }

    private static List<ParsedBirth> Births(CzRow row)
    {
        if (Date(row.BirthDate) is not { } date)
        {
            return [];
        }

        return
        [
            new ParsedBirth
            {
                Year = (short)date.Year,
                Month = (short)date.Month,
                Day = (short)date.Day,
                Precision = DatePrecision.Day,
            },
        ];
    }

    /// <summary>
    /// "Ruská federace", "Ukrajina, Izrael", "dříve Ukrajina, dnes pravděpodobně Ruská
    /// federace". Each part keeps its wording — "formerly" and "probably" are what the
    /// ministry knows — and the country inside it is what gets compared.
    /// </summary>
    private static List<ParsedNationality> Nationalities(string text) =>
    [
        .. CountryParts(text).Select(part => new ParsedNationality
        {
            CountryIso2 = CzCountryNames.Find(part)?.Iso2,
            CountryOriginal = part,
        }),
    ];

    /// <summary>An entity's registered seat is given as a country alone.</summary>
    private static List<ParsedAddress> Seat(string text) =>
    [
        .. CountryParts(text).Select(part => new ParsedAddress
        {
            CountryIso2 = CzCountryNames.Find(part)?.Iso2,
            CountryOriginal = part,
            IsPartial = true,
        }),
    ];

    private static IEnumerable<string> CountryParts(string text) =>
        CountrySeparator().Split(text)
            .Select(p => p.Trim().TrimEnd('.'))
            .Where(p => p.Length > 0);

    /// <summary>
    /// "Usnesení vlády č. 295 ze dne 26. 4. 2023" — the resolution that listed the
    /// subject, numbered within its year. The EU provision the entry cites is the
    /// criteria the subject met, not the act that listed them, and goes to Remarks.
    /// </summary>
    private static List<ParsedLegalInstrument> LegalBasis(string instrument)
    {
        var basis = new List<ParsedLegalInstrument> { Act };
        var match = ResolutionPattern().Match(instrument);

        if (match.Success)
        {
            var date = DateOnly.TryParseExact(
                $"{match.Groups["d"].Value}.{match.Groups["m"].Value}.{match.Groups["y"].Value}",
                "d.M.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
                ? parsed
                : (DateOnly?)null;

            basis.Add(new ParsedLegalInstrument
            {
                Relation = "LISTED_BY",
                InstrumentType = "usnesení vlády",
                InstrumentNumber = $"{match.Groups["n"].Value}/{match.Groups["y"].Value}",
                Title = instrument.Trim(),
                PublicationDate = date,
            });
        }
        else if (instrument.Length > 0)
        {
            basis.Add(new ParsedLegalInstrument
            {
                Relation = "LISTED_BY",
                InstrumentType = "usnesení vlády",
                Title = instrument.Trim(),
            });
        }

        return basis;
    }

    private static string? Remarks(CzRow row)
    {
        var parts = new[]
        {
            Blank(row.StatusNote),
            Blank(row.EuProvision) is { } provision ? "Ustanovení předpisu EU: " + provision : null,
            Blank(row.Measures) is { } measures ? "Uplatňovaná opatření: " + measures : null,
        };

        var joined = string.Join("\n", parts.Where(p => p is not null));
        return joined.Length == 0 ? null : joined;
    }

    private static DateOnly? Date(string cell) =>
        DateOnly.TryParseExact(cell.Trim(), ["d.M.yyyy", "d. M. yyyy"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;

    /// <summary>"ke dni 24. února 2025" and "ke dni 25. 10. 2023" both occur.</summary>
    private static DateOnly? NoteDate(string note)
    {
        var match = NoteDatePattern().Match(note);

        if (!match.Success)
        {
            return null;
        }

        var monthText = match.Groups["m"].Value;
        var month = Months.TryGetValue(monthText, out var named)
            ? named
            : int.TryParse(monthText, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) ? numeric : 0;

        return month is >= 1 and <= 12
            ? new DateOnly(int.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture), month, int.Parse(match.Groups["d"].Value, CultureInfo.InvariantCulture))
            : null;
    }

    private static string? Blank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    [GeneratedRegex(@"ke dni\s+(?<d>\d{1,2})\.\s*(?<m>\p{L}+|\d{1,2})\.?\s*(?<y>\d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NoteDatePattern();

    [GeneratedRegex(@"č\.\s*(?<n>\d+)\s+ze dne\s+(?<d>\d{1,2})\.\s*(?<m>\d{1,2})\.\s*(?<y>\d{4})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ResolutionPattern();

    [GeneratedRegex(@"\s*(?:,|;|\s+a\s+)\s*", RegexOptions.CultureInvariant)]
    private static partial Regex CountrySeparator();
}
