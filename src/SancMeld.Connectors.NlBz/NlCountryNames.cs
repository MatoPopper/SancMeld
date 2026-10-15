using System.Collections.Frozen;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.NlBz;

/// <summary>
/// Countries as the sheet writes them, in Dutch, inside the brackets of a birthplace:
/// "Rabat (Marokko)", "Novi Pazar (voormalig Joegoslavië, huidig Servië)". Every name
/// that occurs in the list as published on 2026-10-15 is here, plus the Dutch names
/// of the countries that commonly appear on sanctions lists. Lookups go through the
/// name normaliser, so case and diacritics do not matter.
/// </summary>
internal static class NlCountryNames
{
    private static readonly (string Phrase, string Iso2)[] Entries =
    [
        ("Nederland", "NL"), ("Marokko", "MA"), ("Turkije", "TR"), ("Irak", "IQ"), ("Somalië", "SO"), ("Egypte", "EG"),
        ("Koeweit", "KW"), ("Rusland", "RU"), ("Russische Federatie", "RU"), ("Tunesië", "TN"), ("Duitsland", "DE"),
        ("Servië", "RS"), ("Algerije", "DZ"), ("Colombia", "CO"), ("Suriname", "SR"), ("Saoedi-Arabië", "SA"),
        ("Bosnië en Herzegovina", "BA"), ("Verenigde Staten", "US"), ("Verenigde Staten van Amerika", "US"), ("Brunei", "BN"),
        ("België", "BE"), ("Frankrijk", "FR"), ("Syrië", "SY"), ("Afghanistan", "AF"), ("Pakistan", "PK"), ("Iran", "IR"),
        ("Libanon", "LB"), ("Jemen", "YE"), ("Libië", "LY"), ("Nigeria", "NG"), ("Indonesië", "ID"), ("Verenigd Koninkrijk", "GB"),
        ("Spanje", "ES"), ("Italië", "IT"), ("Oostenrijk", "AT"), ("Zwitserland", "CH"), ("Polen", "PL"), ("Tsjechië", "CZ"),
        ("Oekraïne", "UA"), ("Wit-Rusland", "BY"), ("Belarus", "BY"), ("Georgië", "GE"), ("Jordanië", "JO"), ("Israël", "IL"),
        ("Soedan", "SD"), ("Eritrea", "ER"), ("Ethiopië", "ET"), ("Kenia", "KE"), ("Mali", "ML"), ("China", "CN"), ("India", "IN"),
        ("Filipijnen", "PH"), ("Kazachstan", "KZ"), ("Oezbekistan", "UZ"), ("Azerbeidzjan", "AZ"), ("Curaçao", "CW"), ("Aruba", "AW"),
        ("Kosovo", "XK"), ("Albanië", "AL"), ("Noord-Macedonië", "MK"), ("Montenegro", "ME"), ("Kroatië", "HR"), ("Griekenland", "GR"),
        ("Qatar", "QA"), ("Verenigde Arabische Emiraten", "AE"), ("Bahrein", "BH"), ("Oman", "OM"), ("Palestina", "PS"), ("Palestijnse Gebieden", "PS"),
    ];

    private static readonly (string Key, string Phrase, string Iso2)[] ByLength =
        [.. Entries.Select(e => (Key: NameNormalizer.Normalize(e.Phrase).Trim(), e.Phrase, e.Iso2)).OrderByDescending(e => e.Key.Length)];

    private static readonly FrozenDictionary<string, string> Iso2ByKey =
        ByLength.ToFrozenDictionary(e => e.Key, e => e.Iso2, StringComparer.Ordinal);

    /// <summary>
    /// The country named last in a phrase — "voormalig Joegoslavië, huidig Servië" is
    /// Serbia today. Returns the dictionary spelling beside the code.
    /// </summary>
    public static (string Iso2, string Phrase)? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var key = NameNormalizer.Normalize(text).Trim();

        if (Iso2ByKey.TryGetValue(key, out var exact))
        {
            return (exact, text.Trim());
        }

        var haystack = " " + key + " ";
        (string Iso2, string Phrase)? best = null;
        var bestEnd = -1;

        foreach (var (phraseKey, phrase, iso2) in ByLength)
        {
            var index = haystack.LastIndexOf(" " + phraseKey + " ", StringComparison.Ordinal);

            if (index >= 0 && index + phraseKey.Length > bestEnd)
            {
                bestEnd = index + phraseKey.Length;
                best = (iso2, phrase);
            }
        }

        return best;
    }
}
