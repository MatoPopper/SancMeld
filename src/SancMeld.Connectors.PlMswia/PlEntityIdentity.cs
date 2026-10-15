using System.Text.RegularExpressions;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// "Dane identyfikacyjne podmiotu": a registered office and whatever registry numbers
/// the ministry had, laid out as a letterhead:
///
///   siedziba:
///   ul. Barkocińska 6 lok. 23
///   03-543 Warszawa
///
///   Nr KRS 0000312056
///   NIP 5222894064
///
/// Numbers are found by their label wherever they sit, including at the end of an
/// address line (one row has the whole block on one line, padded with spaces). What is
/// left after the numbers are taken out is the address. A bracketed block is the same
/// address in Russian script or the previous one, and becomes an address of its own.
/// </summary>
internal static partial class PlEntityIdentity
{
    public sealed record Facts(List<ParsedIdentifier> Identifiers, List<ParsedAddress> Addresses);

    public static Facts Parse(string cell)
    {
        var identifiers = new List<ParsedIdentifier>();
        var mainLines = new List<string>();
        var blocks = new List<List<string>>();
        List<string>? block = null;

        foreach (var raw in cell.Split('\n'))
        {
            var line = raw.Trim();

            if (block is not null)
            {
                block.Add(line.TrimEnd(')', ',', ' '));
                if (line.EndsWith(')') || line.EndsWith("),"))
                {
                    block = null;
                }

                continue;
            }

            if (line.StartsWith('('))
            {
                block = [line.TrimStart('(').TrimEnd(')', ',', ' ')];
                blocks.Add(block);
                if (line.EndsWith(')') || line.EndsWith("),"))
                {
                    block = null;
                }

                continue;
            }

            var remainder = line;

            foreach (Match id in IdentifierPattern().Matches(line))
            {
                Add(identifiers, id.Groups["label"].Value, id.Groups["value"].Value);
                remainder = remainder.Replace(id.Value, " ", StringComparison.Ordinal);
            }

            remainder = AddressLeadPattern().Replace(remainder, string.Empty);
            remainder = Collapse(remainder).Trim(',', ' ', ';');

            if (remainder.Length > 0 && !LeftoverPattern().IsMatch(remainder))
            {
                mainLines.Add(remainder);
            }
        }

        var addresses = new List<ParsedAddress>();
        var main = Address(string.Join(", ", mainLines), inheritFrom: null);

        if (main is not null)
        {
            addresses.Add(main);
        }

        foreach (var lines in blocks)
        {
            var text = BlockLeadPattern().Replace(string.Join(", ", lines.Where(l => l.Length > 0)), string.Empty).Trim(',', ' ');

            // "(nazwa rosyjskojęzyczna: ул. Воронянского, 35, …)" is the same office in
            // the script its own country writes it in; "(poprzedni adres: …)" the one
            // before. Either is somewhere the subject can be found.
            if (Address(text, inheritFrom: main) is { } address)
            {
                addresses.Add(address);
            }
        }

        return new Facts(identifiers, addresses);
    }

    private static ParsedAddress? Address(string text, ParsedAddress? inheritFrom)
    {
        if (text.Length == 0)
        {
            return null;
        }

        var country = PlCountryNames.Find(text);

        // Polish offices are written without a country, and the postcode is how a reader
        // knows: 03-543 is a format no other country in the list uses.
        var iso2 = country?.Iso2 ?? (PolishPostcodePattern().IsMatch(text) ? "PL" : inheritFrom?.CountryIso2);

        return new ParsedAddress
        {
            Street = text,
            CountryIso2 = iso2,
            CountryOriginal = country?.Phrase ?? (iso2 is null ? null : inheritFrom?.CountryOriginal),
            IsPartial = false,
        };
    }

    private static void Add(List<ParsedIdentifier> identifiers, string label, string value)
    {
        var normalized = IdentifierNormalizer.Normalize(value);

        if (normalized.Length == 0)
        {
            return;
        }

        var key = Collapse(label).ToUpperInvariant();

        // KRS, NIP and REGON are Polish registers; INN, OGRN, KPP and OKPO Russian; BIN
        // Kazakh; UNP Belarusian. Each is the country's own and says where the company is
        // registered more reliably than the address does.
        var (kind, country) = key switch
        {
            "KRS" => (IdentifierType.CompanyRegistration, "PL"),
            "NIP" => (IdentifierType.TaxId, "PL"),
            "REGON" => (IdentifierType.Other, "PL"),
            "INN" => (IdentifierType.TaxId, "RU"),
            "KPP" => (IdentifierType.TaxId, "RU"),
            "OGRN" => (IdentifierType.CompanyRegistration, "RU"),
            "OKPO" => (IdentifierType.Other, "RU"),
            "BIN" => (IdentifierType.CompanyRegistration, "KZ"),
            "UNP" => (IdentifierType.TaxId, "BY"),
            "TIN" => (IdentifierType.TaxId, null),
            "DUNS" => (IdentifierType.Other, null),
            _ => (IdentifierType.CompanyRegistration, null),
        };

        if (identifiers.Any(i => i.Kind == kind && i.ValueNormalized == normalized))
        {
            return;
        }

        identifiers.Add(new ParsedIdentifier
        {
            Kind = kind,
            ValueOriginal = value,
            ValueNormalized = normalized,
            IssuingCountry = country,
            Note = label.Trim(),
        });
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\t', '\r', ' '], StringSplitOptions.RemoveEmptyEntries));

    /// <summary>
    /// A label, an optional colon, and a value that contains at least one digit — so that
    /// in "numer identyfikacyjny BIN: 120240019110" the value is the number and not the
    /// word BIN, which the next alternative then reads as the label it is.
    /// </summary>
    [GeneratedRegex(
        @"(?<![\p{L}])(?<label>Busines{1,2} Registration Number|rejestr handlowy nr|pod numerem|numer rejestrowy|nr rejestrowy|numer rejestracyjny|kod przedsiębiorstwa|numer identyfikacyjny|nr identyfikacyjny|KRS|NIP|REGON|DUNS|INN|OGRN|KPP|OKPO|BIN|UNP|TIN)(?![\p{L}])\s*[:：]?\s*(?<value>(?=[\p{L}\d\- ]*\d)[\p{L}\d][\p{L}\d\-]*(?: \d[\d\-]*)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex IdentifierPattern();

    [GeneratedRegex(@"^(?:siedziba|adres)\s*:\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AddressLeadPattern();

    [GeneratedRegex(@"^(?:nazwa rosyjskojęzyczna|adres w języku rosyjskim|adres w jęz\.\s*rosyjskim|adres rosyjskojęzyczny|poprzedni adres|poprzednio)\s*:?\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex BlockLeadPattern();

    /// <summary>What a line is once its numbers are gone: "Nr", "numer", punctuation.</summary>
    [GeneratedRegex(@"^(?:nr|numer|nr\.|,|;|:|\s)*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LeftoverPattern();

    [GeneratedRegex(@"(?<!\d)\d{2}-\d{3}(?!\d)")]
    private static partial Regex PolishPostcodePattern();
}
