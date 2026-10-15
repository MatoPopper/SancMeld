using System.Collections.Frozen;
using System.Text.RegularExpressions;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.UkFcdo;

/// <summary>
/// Countries as the list writes them: English short names, with the publisher's own
/// habits — "DPRK" and "North Korea" both, "Congo (Democratic Republic)", "The Gambia",
/// "St Kitts and Nevis", "Palestinian" as a nationality — and, for places of birth, the
/// state that existed at the time with the present one in brackets: "Former USSR (now
/// Belarus)", "Kazakh SSR", "Russian SFSR, (now Russian Federation)".
///
/// Every name that occurs in the list as published on 2026-10-15 is here (109
/// nationalities, 131 countries of birth, 107 address countries). "Former USSR" alone,
/// "Occupied Golan Heights" and "Kosovo" get no code: the first names no present state,
/// the second is contested, the third has no ISO 3166-1 code, and inventing one would
/// put a political claim into a comparison. They keep their wording and compare as
/// "not recorded".
/// </summary>
internal static partial class UkCountryNames
{
    private static readonly (string Phrase, string Iso2)[] Entries =
    [
        ("Afghanistan", "AF"), ("Albania", "AL"), ("Algeria", "DZ"), ("Angola", "AO"), ("Armenia", "AM"), ("Australia", "AU"),
        ("Austria", "AT"), ("Azerbaijan", "AZ"), ("Bangladesh", "BD"), ("Belarus", "BY"), ("Belgium", "BE"),
        ("Bosnia and Herzegovina", "BA"), ("Brazil", "BR"), ("Bulgaria", "BG"), ("Cambodia", "KH"), ("Cameroon", "CM"),
        ("Canada", "CA"), ("Cape Verde", "CV"), ("Central African Republic", "CF"), ("Chad", "TD"), ("China", "CN"),
        ("Colombia", "CO"), ("Comoros", "KM"), ("Congo", "CG"), ("Congo (Democratic Republic)", "CD"),
        ("Democratic Republic of the Congo", "CD"), ("Croatia", "HR"), ("Cuba", "CU"), ("Cyprus", "CY"), ("Czechia", "CZ"),
        ("Czech Republic", "CZ"), ("DPRK", "KP"), ("North Korea", "KP"), ("Democratic People's Republic of Korea", "KP"),
        ("Ecuador", "EC"), ("Egypt", "EG"), ("El Salvador", "SV"), ("Equatorial Guinea", "GQ"), ("Eritrea", "ER"),
        ("Estonia", "EE"), ("Ethiopia", "ET"), ("Finland", "FI"), ("France", "FR"), ("Gabon", "GA"), ("Georgia", "GE"),
        ("Germany", "DE"), ("Former German Democratic Republic", "DE"), ("Greece", "GR"), ("Guatemala", "GT"), ("Guinea", "GN"),
        ("Guinea-Bissau", "GW"), ("Haiti", "HT"), ("Honduras", "HN"), ("Hungary", "HU"), ("India", "IN"), ("Indonesia", "ID"),
        ("Iran", "IR"), ("Iraq", "IQ"), ("Ireland", "IE"), ("Israel", "IL"), ("Italy", "IT"), ("Japan", "JP"), ("Jordan", "JO"),
        ("Kazakhstan", "KZ"), ("Kenya", "KE"), ("Kuwait", "KW"), ("Kyrgyzstan", "KG"), ("Laos", "LA"), ("Latvia", "LV"),
        ("Lebanon", "LB"), ("Libya", "LY"), ("Liechtenstein", "LI"), ("Lithuania", "LT"), ("Luxembourg", "LU"),
        ("Malaysia", "MY"), ("Mali", "ML"), ("Malta", "MT"), ("Marshall Islands", "MH"), ("Mauritania", "MR"),
        ("Mauritius", "MU"), ("Moldova", "MD"), ("Republic of Moldova", "MD"), ("Republic of Moldovia", "MD"),
        ("Mongolia", "MN"), ("Montenegro", "ME"), ("Morocco", "MA"), ("Myanmar", "MM"), ("Netherlands", "NL"),
        ("New Zealand", "NZ"), ("Nicaragua", "NI"), ("Niger", "NE"), ("Nigeria", "NG"), ("Norway", "NO"), ("Oman", "OM"),
        ("Pakistan", "PK"), ("Palestinian", "PS"), ("Occupied Palestinian Territories", "PS"), ("Palestine", "PS"),
        ("Panama", "PA"), ("Philippines", "PH"), ("Poland", "PL"), ("Portugal", "PT"), ("Qatar", "QA"), ("Romania", "RO"),
        ("Russia", "RU"), ("Russian Federation", "RU"), ("Russia (USSR)", "RU"), ("Rwanda", "RW"), ("Saudi Arabia", "SA"),
        ("Senegal", "SN"), ("Serbia", "RS"), ("Sierra Leone", "SL"), ("Singapore", "SG"), ("Slovakia", "SK"), ("Somalia", "SO"),
        ("South Africa", "ZA"), ("South Korea", "KR"), ("South Sudan", "SS"), ("Spain", "ES"), ("Sri Lanka", "LK"),
        ("St Kitts and Nevis", "KN"), ("Saint Kitts and Nevis", "KN"), ("St Lucia", "LC"), ("Saint Lucia", "LC"), ("Sudan", "SD"),
        ("Sweden", "SE"), ("Switzerland", "CH"), ("Syria", "SY"), ("Tajikistan", "TJ"), ("Tanzania", "TZ"), ("Thailand", "TH"),
        ("The Gambia", "GM"), ("Gambia", "GM"), ("Trinidad and Tobago", "TT"), ("Tunisia", "TN"), ("Turkey", "TR"),
        ("Uganda", "UG"), ("Ukraine", "UA"), ("United Arab Emirates", "AE"), ("United Kingdom", "GB"), ("United States", "US"),
        ("Uzbekistan", "UZ"), ("Vanuatu", "VU"), ("Venezuela", "VE"), ("Vietnam", "VN"), ("Yemen", "YE"), ("Zimbabwe", "ZW"),

        // Flags of convenience: where the sanctioned ships are registered.
        ("Barbados", "BB"), ("Palau", "PW"), ("Cook Islands", "CK"), ("Antigua and Barbuda", "AG"), ("Djibouti", "DJ"),
        ("Liberia", "LR"), ("San Marino", "SM"), ("St Vincent", "VC"), ("St Vincent and the Grenadines", "VC"), ("Dominica", "DM"),
        ("Togo", "TG"), ("Mozambique", "MZ"), ("Belize", "BZ"), ("Panama", "PA"), ("Gabon", "GA"), ("Sierra Leone", "SL"),
        ("Cameroon", "CM"), ("Tanzania", "TZ"), ("Honduras", "HN"), ("Moldova", "MD"), ("Mongolia", "MN"), ("Tuvalu", "TV"),
        ("Samoa", "WS"), ("Bahamas", "BS"), ("Malta", "MT"), ("Cyprus", "CY"), ("Hong Kong", "HK"), ("Eswatini", "SZ"),
    ];

    private static readonly (string Key, string Phrase, string Iso2)[] ByLength =
        [.. Entries.Select(e => (Key: NameNormalizer.Normalize(e.Phrase).Trim(), e.Phrase, e.Iso2)).DistinctBy(e => e.Key).OrderByDescending(e => e.Key.Length)];

    private static readonly FrozenDictionary<string, string> Iso2ByKey =
        ByLength.ToFrozenDictionary(e => e.Key, e => e.Iso2, StringComparer.Ordinal);

    public static (string Iso2, string Phrase)? Find(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        // "Former USSR (now Belarus)", "Kyrgyz SSR (now Kyrgyzstan)", "former USSR
        // Currently Russia": the present state is the one to compare against.
        if (NowPattern().Match(text) is { Success: true } now)
        {
            return Find(now.Groups["country"].Value);
        }

        var key = NameNormalizer.Normalize(text).Trim();

        if (Iso2ByKey.TryGetValue(key, out var exact))
        {
            return (exact, text.Trim());
        }

        // "Kazakh SSR", "Ukrainian SSR", "Yakut Autonomous SSR" with no present state
        // named: the SSR's successor, by its adjective.
        if (SsrPattern().Match(key) is { Success: true } ssr)
        {
            var successor = ssr.Groups["adj"].Value switch
            {
                "kazakh" => "KZ",
                "ukrainian" => "UA",
                "belarusian" or "byelorussian" => "BY",
                "kyrgyz" or "kirghiz" => "KG",
                "uzbekistan" or "uzbek" => "UZ",
                "moldovan" or "moldovian" or "moldavian" => "MD",
                "russian" => "RU",
                "yakut autonomous" or "komi" => "RU",
                "georgian" => "GE",
                "armenian" => "AM",
                "azerbaijan" or "azerbaijani" => "AZ",
                "tajik" => "TJ",
                "turkmen" => "TM",
                "latvian" => "LV",
                "lithuanian" => "LT",
                "estonian" => "EE",
                _ => null,
            };

            return successor is null ? null : (successor, text.Trim());
        }

        return null;
    }

    [GeneratedRegex(@"(?:\bnow\b|\bcurrently\b)\s*:?\s*(?<country>[^()]+?)\s*\)?\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex NowPattern();

    [GeneratedRegex(@"^(?<adj>[a-z ]+?)\s+(?:sfsr|ssr|soviet socialist republic|soviet federative socialist republic)\b", RegexOptions.CultureInvariant)]
    private static partial Regex SsrPattern();
}
