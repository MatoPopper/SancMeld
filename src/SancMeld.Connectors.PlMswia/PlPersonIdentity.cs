using System.Globalization;
using System.Text.RegularExpressions;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// "Dane identyfikacyjne osoby", one sentence per line:
///
///   urodzony 15 listopada 1963 r. w Wilnie (Litwa)
///   obywatel rosyjski
///   nr PESEL: 63111513931
///
/// On 445 persons (2026-10-15) 434 lines start with "urodzony"/"urodzona", 8 state a
/// nationality, 4 a PESEL and 2 a TIN. Anything this does not recognise is still in
/// the raw record; nothing is invented for it.
/// </summary>
internal static partial class PlPersonIdentity
{
    public sealed record Facts(
        List<ParsedBirth> Births,
        List<ParsedNationality> Nationalities,
        List<ParsedIdentifier> Identifiers);

    private static readonly Dictionary<string, short> Months = new(StringComparer.OrdinalIgnoreCase)
    {
        ["stycznia"] = 1,
        ["lutego"] = 2,
        ["marca"] = 3,
        ["kwietnia"] = 4,
        ["maja"] = 5,
        ["czerwca"] = 6,
        ["lipca"] = 7,
        ["sierpnia"] = 8,
        ["września"] = 9,
        ["października"] = 10,
        ["listopada"] = 11,
        ["grudnia"] = 12,
    };

    public static Facts Parse(string cell)
    {
        var facts = new Facts([], [], []);

        foreach (var raw in cell.Split('\n'))
        {
            var line = raw.Trim().TrimEnd(',', ' ');

            // "(obywatel Armenii)" and "(TIN: 246403033635)" are whole lines in brackets;
            // "urodzony 15 listopada 1963 r. w Wilnie (Litwa)" is not, and its closing
            // bracket belongs to the country.
            if (line.StartsWith('(') && line.EndsWith(')'))
            {
                line = line[1..^1].Trim();
            }

            if (line.Length == 0)
            {
                continue;
            }

            if (BirthPattern().Match(line) is { Success: true } birth)
            {
                facts.Births.Add(Birth(birth));
            }
            else if (line.StartsWith("w ", StringComparison.OrdinalIgnoreCase) && facts.Births.Count > 0)
            {
                // "urodzony 27 sierpnia 1969 r." on one line and "w Kaliningradzie
                // (Federacja Rosyjska)" on the next: the place of the date just read.
                var (place, country) = PlaceAndCountry(line[2..]);
                var last = facts.Births[^1];
                facts.Births[^1] = last with { PlaceOriginal = place, CountryOriginal = country, CountryIso2 = PlCountryNames.ToIso2(country) };
            }

            if (NationalityPattern().Match(line) is { Success: true } nationality)
            {
                foreach (var phrase in ConjunctionPattern().Split(nationality.Groups["countries"].Value))
                {
                    var country = phrase.Trim().TrimEnd('.', ',');

                    if (country.Length > 0)
                    {
                        facts.Nationalities.Add(new ParsedNationality
                        {
                            CountryIso2 = PlCountryNames.ToIso2(country),
                            CountryOriginal = country,
                        });
                    }
                }
            }

            foreach (Match id in IdentifierPattern().Matches(line))
            {
                var value = id.Groups["value"].Value;
                var normalized = IdentifierNormalizer.Normalize(value);

                if (normalized.Length == 0)
                {
                    continue;
                }

                var label = id.Groups["label"].Value.ToUpperInvariant();

                facts.Identifiers.Add(new ParsedIdentifier
                {
                    Kind = label switch
                    {
                        "PESEL" => IdentifierType.NationalId,
                        "TIN" => IdentifierType.TaxId,
                        _ => IdentifierType.Passport,
                    },
                    ValueOriginal = value,
                    ValueNormalized = normalized,

                    // PESEL is Poland's and nobody else's. A TIN is whichever country's the
                    // ministry had in front of it, and the file does not say.
                    IssuingCountry = label == "PESEL" ? "PL" : null,
                    Note = label,
                });
            }
        }

        return facts;
    }

    /// <summary>
    /// "urodzony 5 października 1973 r." is a day; "urodzony w 1966 roku" a year; "urodzona
    /// 18.05.1992 r." a day in digits. The precision follows what was written, or a year
    /// alone would arrive as the first of January and contradict every real date.
    /// </summary>
    private static ParsedBirth Birth(Match match)
    {
        var year = short.Parse(match.Groups["year"].Value, CultureInfo.InvariantCulture);
        short? day = match.Groups["day"].Success ? short.Parse(match.Groups["day"].Value, CultureInfo.InvariantCulture) : null;
        short? month = null;

        if (match.Groups["month"].Success)
        {
            var written = match.Groups["month"].Value;
            month = Months.TryGetValue(written, out var named)
                ? named
                : short.TryParse(written, NumberStyles.None, CultureInfo.InvariantCulture, out var numeric) ? numeric : null;
        }

        var (place, country) = match.Groups["place"].Success
            ? PlaceAndCountry(match.Groups["place"].Value)
            : (null, null);

        return new ParsedBirth
        {
            Year = year,
            Month = month,
            Day = month is null ? null : day,
            Precision = (month, day) switch
            {
                (not null, not null) => DatePrecision.Day,
                (not null, null) => DatePrecision.Month,
                _ => DatePrecision.Year,
            },
            PlaceOriginal = place,
            CountryOriginal = country,
            CountryIso2 = PlCountryNames.ToIso2(country),
        };
    }

    /// <summary>
    /// "Wilnie (Litwa)", "miejscowości Łuniniec (Luniniec), Republika Białorusi",
    /// "Moskwie". The place keeps the publisher's locative case: it is a label for a
    /// reader, and nothing compares it.
    /// </summary>
    private static (string? Place, string? Country) PlaceAndCountry(string text)
    {
        var place = PlaceLeadPattern().Replace(text.Trim().TrimEnd('.', ',', ' '), string.Empty);

        if (place.Length == 0)
        {
            return (null, null);
        }

        var comma = place.LastIndexOf(',');
        if (comma > 0 && PlCountryNames.ToIso2(place[(comma + 1)..].Trim()) is not null)
        {
            return (place[..comma].Trim(), place[(comma + 1)..].Trim());
        }

        if (place.EndsWith(')') && place.LastIndexOf('(') is var open and > 0
            && PlCountryNames.ToIso2(place[(open + 1)..^1].Trim()) is not null)
        {
            return (place[..open].Trim(), place[(open + 1)..^1].Trim());
        }

        return (place, null);
    }

    [GeneratedRegex(
        @"^urodzon\p{L}*\s+(?:w\s+)?(?:(?<day>\d{1,2})[ .]\s*(?<month>\p{L}+|\d{1,2})[ .]\s*)?(?<year>\d{4})(?:\s*r(?:oku|\.)?)?(?:[\s,]+w\s+(?<place>.+?))?[\s,.]*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BirthPattern();

    [GeneratedRegex(@"(?:obywatel(?:ka|a)?|ob\.)\s+(?<countries>.+)$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NationalityPattern();

    [GeneratedRegex(@"\s+(?:i|oraz)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex ConjunctionPattern();

    [GeneratedRegex(@"(?<label>PESEL|TIN|paszport\p{L}*)\s*(?:nr|numer)?\s*[:：]?\s*(?<value>[0-9A-Z][0-9A-Z-]{4,})", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^(?:miejscowości|mieście|m\.)\s+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PlaceLeadPattern();
}
