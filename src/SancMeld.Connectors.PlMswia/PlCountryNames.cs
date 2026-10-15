using System.Collections.Frozen;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// Countries as the list writes them: Polish, inflected, and occasionally English.
///
/// A nationality is "obywatel Republiki Białorusi" (genitive), a birthplace "w Wilnie
/// (Litwa)" (nominative, in brackets), a registered office ends ", Federacja Rosyjska" or
/// ", Cyprus". Without this the country qualifier of every Polish listing is text nobody
/// can compare, and "the country you supplied agrees with the listing" cannot be answered.
///
/// Every form here occurs in the list as published on 2026-10-15, plus the nominative of
/// each country that occurred only inflected. Lookups go through the name normaliser, so
/// case and diacritics do not matter and "Bialorus" finds BY.
/// </summary>
internal static class PlCountryNames
{
    private static readonly (string Phrase, string Iso2)[] Entries =
    [
        ("Rosja", "RU"), ("Rosji", "RU"), ("Federacja Rosyjska", "RU"), ("Federacji Rosyjskiej", "RU"),
        ("rosyjski", "RU"), ("rosyjska", "RU"), ("Russia", "RU"), ("Russian Federation", "RU"),
        ("Białoruś", "BY"), ("Białorusi", "BY"), ("Republika Białorusi", "BY"), ("Republiki Białorusi", "BY"),
        ("białoruski", "BY"), ("białoruska", "BY"), ("Belarus", "BY"),
        ("Ukraina", "UA"), ("Ukrainy", "UA"), ("ukraiński", "UA"), ("ukraińska", "UA"), ("Ukraine", "UA"),
        ("Polska", "PL"), ("Polski", "PL"), ("Rzeczpospolita Polska", "PL"), ("Poland", "PL"),
        ("Cypr", "CY"), ("Cypru", "CY"), ("Republika Cypryjska", "CY"), ("Cyprus", "CY"),
        ("Litwa", "LT"), ("Litwy", "LT"), ("Lithuania", "LT"),
        ("Łotwa", "LV"), ("Łotwy", "LV"), ("Latvia", "LV"),
        ("Estonia", "EE"), ("Estonii", "EE"),
        ("Kazachstan", "KZ"), ("Kazachstanu", "KZ"), ("Kazakhstan", "KZ"),
        ("Uzbekistan", "UZ"), ("Uzbekistanu", "UZ"),
        ("Kirgistan", "KG"), ("Tadżykistan", "TJ"), ("Turkmenistan", "TM"), ("Azerbejdżan", "AZ"), ("Azerbejdżanu", "AZ"),
        ("Mołdawia", "MD"), ("Mołdawii", "MD"), ("Republika Mołdawii", "MD"), ("Moldova", "MD"),
        ("Izrael", "IL"), ("Izraela", "IL"), ("Israel", "IL"),
        ("Armenia", "AM"), ("Armenii", "AM"), ("Gruzja", "GE"), ("Gruzji", "GE"),
        ("Węgry", "HU"), ("Węgier", "HU"), ("Hungary", "HU"),
        ("Luksemburg", "LU"), ("Luksemburga", "LU"), ("Luxembourg", "LU"),
        ("Szwajcaria", "CH"), ("Szwajcarii", "CH"), ("Switzerland", "CH"),
        ("Turcja", "TR"), ("Turcji", "TR"), ("Turkey", "TR"), ("Türkiye", "TR"),
        ("Zjednoczone Emiraty Arabskie", "AE"), ("ZEA", "AE"), ("United Arab Emirates", "AE"),
        ("Niemcy", "DE"), ("Niemiec", "DE"), ("Germany", "DE"),
        ("Czechy", "CZ"), ("Czech", "CZ"), ("Republika Czeska", "CZ"),
        ("Słowacja", "SK"), ("Słowacji", "SK"),
        ("Austria", "AT"), ("Austrii", "AT"),
        ("Chiny", "CN"), ("Chin", "CN"), ("Chińska Republika Ludowa", "CN"), ("China", "CN"),
        ("Hongkong", "HK"), ("Hong Kong", "HK"), ("Singapur", "SG"), ("Singapore", "SG"),
        ("Indie", "IN"), ("Indii", "IN"), ("Iran", "IR"), ("Iranu", "IR"),
        ("Wielka Brytania", "GB"), ("Wielkiej Brytanii", "GB"), ("Zjednoczone Królestwo", "GB"), ("United Kingdom", "GB"),
        ("Holandia", "NL"), ("Holandii", "NL"), ("Niderlandy", "NL"), ("Netherlands", "NL"),
        ("Belgia", "BE"), ("Belgii", "BE"), ("Francja", "FR"), ("Francji", "FR"), ("France", "FR"),
        ("Włochy", "IT"), ("Włoch", "IT"), ("Italy", "IT"), ("Hiszpania", "ES"), ("Hiszpanii", "ES"),
        ("Malta", "MT"), ("Malty", "MT"), ("Grecja", "GR"), ("Grecji", "GR"),
        ("Serbia", "RS"), ("Serbii", "RS"), ("Bułgaria", "BG"), ("Bułgarii", "BG"), ("Rumunia", "RO"), ("Rumunii", "RO"),
        ("Szwecja", "SE"), ("Szwecji", "SE"), ("Finlandia", "FI"), ("Finlandii", "FI"), ("Norwegia", "NO"), ("Norwegii", "NO"), ("Dania", "DK"), ("Danii", "DK"),
        ("Irlandia", "IE"), ("Irlandii", "IE"), ("Portugalia", "PT"), ("Portugalii", "PT"), ("Chorwacja", "HR"), ("Słowenia", "SI"),
        ("Liechtenstein", "LI"), ("Monako", "MC"), ("Gibraltar", "GI"), ("Jersey", "JE"),
        ("Stany Zjednoczone", "US"), ("USA", "US"), ("United States", "US"), ("Kanada", "CA"), ("Kanady", "CA"),
        ("Seszele", "SC"), ("Panama", "PA"), ("Panamy", "PA"), ("Brytyjskie Wyspy Dziewicze", "VG"), ("Wyspy Marshalla", "MH"),
        ("Mongolia", "MN"), ("Wietnam", "VN"), ("Japonia", "JP"), ("Korea Południowa", "KR"), ("Korea Północna", "KP"),
        ("Egipt", "EG"), ("Syria", "SY"), ("Syrii", "SY"), ("Irak", "IQ"), ("Iraku", "IQ"), ("Afganistan", "AF"), ("Afganistanu", "AF"),
    ];

    private static readonly FrozenDictionary<string, string> Iso2ByKey =
        Entries.ToFrozenDictionary(e => Key(e.Phrase), e => e.Iso2, StringComparer.Ordinal);

    /// <summary>Longest phrases first, so "Republika Białorusi" wins over "Białorusi" inside it.</summary>
    private static readonly (string Key, string Phrase, string Iso2)[] ByLength =
        [.. Entries.Select(e => (Key: Key(e.Phrase), e.Phrase, e.Iso2)).OrderByDescending(e => e.Key.Length)];

    /// <summary>The whole phrase is a country, in any case or inflection listed above.</summary>
    public static string? ToIso2(string? phrase)
    {
        if (string.IsNullOrWhiteSpace(phrase))
        {
            return null;
        }

        return Iso2ByKey.TryGetValue(Key(phrase), out var iso2) ? iso2 : null;
    }

    /// <summary>
    /// The country named last in a free-text address — the convention the list follows,
    /// "ul. Dokuka 9, 236019 Kaliningrad, obwód kaliningradzki, Federacja Rosyjska".
    /// Returns the phrase in its dictionary spelling beside the code.
    /// </summary>
    public static (string Iso2, string Phrase)? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var haystack = " " + Key(text) + " ";
        (string Iso2, string Phrase)? best = null;
        var bestEnd = -1;

        // Compared by where the phrase ends, longest phrases first: "Federacja Rosyjska"
        // and the "Rosyjska" inside it end at the same place, and the tie goes to the
        // whole name rather than to its last word.
        foreach (var (key, phrase, iso2) in ByLength)
        {
            var index = haystack.LastIndexOf(" " + key + " ", StringComparison.Ordinal);

            if (index >= 0 && index + key.Length > bestEnd)
            {
                bestEnd = index + key.Length;
                best = (iso2, phrase);
            }
        }

        return best;
    }

    private static string Key(string phrase) => NameNormalizer.Normalize(phrase).Trim();
}
