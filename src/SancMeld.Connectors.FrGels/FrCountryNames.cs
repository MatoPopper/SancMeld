using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace SancMeld.Connectors.FrGels;

/// <summary>
/// The register writes countries as French names in capitals, never as codes.
///
/// Without this the nationality of every French listing arrives as text nobody can
/// compare, and "the country you supplied agrees with the listing" — one of the four
/// qualifiers a check answers — is unanswerable for the largest source of cross-list
/// training pairs we have. The whole point of importing this register is that it records
/// the same subjects as the EU with independently recorded qualifiers; losing one of them
/// to a vocabulary mismatch would be self-defeating.
///
/// Every entry here occurs in the published register (120 distinct names on 2026-08-19),
/// and the mapping is checked against the publisher's own cross-references: where France
/// and the EU describe the same subject, their countries must agree.
///
/// Two names are deliberately absent. KOSOVO has no ISO 3166-1 code, and KURDISTAN
/// IRAKIEN is a region rather than a country; inventing codes for them would put a
/// contested political claim into a comparison. They keep their wording and compare as
/// "not recorded", which is the truth.
/// </summary>
internal static class FrCountryNames
{
    public static string? ToIso2(string? french)
    {
        if (string.IsNullOrWhiteSpace(french))
        {
            return null;
        }

        return Iso2ByName.TryGetValue(Key(french), out var iso2) ? iso2 : null;
    }

    /// <summary>
    /// Accents and apostrophes are how this lookup would fail silently: the register
    /// writes CÔTE D'IVOIRE with a typographic apostrophe in some releases and a straight
    /// one in others, and a table keyed on one spelling would quietly stop matching.
    /// </summary>
    private static string Key(string french)
    {
        var decomposed = french.Trim().ToUpperInvariant().Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            if (char.IsLetterOrDigit(ch) || ch == '/') sb.Append(ch);
            else if (sb.Length > 0 && sb[^1] != ' ') sb.Append(' ');
        }

        return sb.ToString().TrimEnd();
    }

    private static readonly FrozenDictionary<string, string> Iso2ByName = new Dictionary<string, string>
    {
        ["RUSSIE"] = "RU", ["IRAN"] = "IR", ["BIELORUSSIE"] = "BY", ["AFGHANISTAN"] = "AF",
        ["SYRIE"] = "SY", ["UKRAINE"] = "UA", ["IRAK"] = "IQ", ["BIRMANIE/MYANMAR"] = "MM",
        ["COREE DU NORD"] = "KP", ["TUNISIE"] = "TN", ["REPUBLIQUE DEMOCRATIQUE DU CONGO"] = "CD",
        ["PAKISTAN"] = "PK", ["LIBYE"] = "LY", ["CHINE"] = "CN", ["FRANCE"] = "FR",
        ["MOLDAVIE"] = "MD", ["ALGERIE"] = "DZ", ["INDONESIE"] = "ID",
        ["EMIRATS ARABES UNIS"] = "AE", ["SOUDAN"] = "SD", ["PHILIPPINES"] = "PH",
        ["YEMEN"] = "YE", ["RWANDA"] = "RW", ["ROYAUME UNI"] = "GB", ["SOMALIE"] = "SO",
        ["REPUBLIQUE CENTRAFRICAINE"] = "CF", ["EGYPTE"] = "EG", ["TURQUIE"] = "TR",
        ["ARABIE SAOUDITE"] = "SA", ["MALI"] = "ML", ["ALLEMAGNE"] = "DE",
        ["VENEZUELA"] = "VE", ["NICARAGUA"] = "NI", ["HAITI"] = "HT", ["KAZAKHSTAN"] = "KZ",
        ["ETATS UNIS"] = "US", ["JORDANIE"] = "JO", ["OUGANDA"] = "UG", ["MAROC"] = "MA",
        ["SOUDAN DU SUD"] = "SS", ["LIBAN"] = "LB", ["ISRAEL"] = "IL", ["KOWEIT"] = "KW",
        ["OUZBEKISTAN"] = "UZ", ["MALAISIE"] = "MY", ["TERRITOIRES PALESTINIENS"] = "PS",
        ["KENYA"] = "KE", ["GEORGIE"] = "GE", ["ITALIE"] = "IT", ["HONG KONG"] = "HK",
        ["QATAR"] = "QA", ["SUISSE"] = "CH", ["KIRGHIZISTAN"] = "KG", ["AZERBAIDJAN"] = "AZ",
        ["LETTONIE"] = "LV", ["MAURITANIE"] = "MR", ["GUATEMALA"] = "GT",
        ["TRINIDAD ET TOBAGO"] = "TT", ["ETAT DE PALESTINE"] = "PS", ["INDE"] = "IN",
        ["TANZANIE"] = "TZ", ["ARMENIE"] = "AM", ["COLOMBIE"] = "CO", ["BELGIQUE"] = "BE",
        ["REPUBLIQUE TCHEQUE"] = "CZ", ["NIGERIA"] = "NG", ["BOSNIE HERZEGOVINE"] = "BA",
        ["TCHAD"] = "TD", ["CANADA"] = "CA", ["SINGAPOUR"] = "SG", ["TADJIKISTAN"] = "TJ",
        ["ERYTHREE"] = "ER", ["CAMBODGE"] = "KH", ["AUSTRALIE"] = "AU", ["ROUMANIE"] = "RO",
        ["LITUANIE"] = "LT", ["MAURICE"] = "MU", ["FINLANDE"] = "FI", ["ETHIOPIE"] = "ET",
        ["VIETNAM"] = "VN", ["SERBIE"] = "RS", ["CAMEROUN"] = "CM", ["SUEDE"] = "SE",
        ["NORVEGE"] = "NO", ["ESTONIE"] = "EE", ["TOGO"] = "TG", ["BURUNDI"] = "BI",
        ["JERSEY"] = "JE", ["COTE D IVOIRE"] = "CI", ["OMAN"] = "OM", ["PAYS BAS"] = "NL",
        ["BURKINA FASO"] = "BF", ["BANGLADESH"] = "BD", ["NIGER"] = "NE", ["SENEGAL"] = "SN",
        ["ILES CAIMANES"] = "KY", ["SAINT KITTS ET NEVIS"] = "KN", ["LIBERIA"] = "LR",
        ["CHYPRE"] = "CY", ["MONTENEGRO"] = "ME", ["HONDURAS"] = "HN", ["SLOVENIE"] = "SI",
        ["BAHREIN"] = "BH", ["CROATIE"] = "HR", ["ILES MARSHALL"] = "MH",
        ["SEYCHELLES"] = "SC", ["LIECHTENSTEIN"] = "LI", ["COMORES"] = "KM",
        ["PANAMA"] = "PA", ["ALBANIE"] = "AL", ["HONGRIE"] = "HU", ["ESPAGNE"] = "ES",
        ["GUINEE"] = "GN", ["GABON"] = "GA", ["BELIZE"] = "BZ", ["CONGO"] = "CG",
        ["ILES VIERGES BRITANNIQUES"] = "VG", ["JAPON"] = "JP",
    }.ToFrozenDictionary();
}
