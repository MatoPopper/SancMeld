using System.Collections.Frozen;
namespace SancMeld.Core.Matching;

/// <summary>
/// Countries as the list writes them: Czech, sometimes qualified ("dříve Ukrajina, dnes
/// pravděpodobně Ruská federace"). The country qualifier of a Czech listing has to be
/// comparable, or "the country you supplied agrees with the listing" has no answer.
///
/// Every name that occurs in the list as published on 2026-10-15 is here, plus the
/// Czech names of the countries that commonly appear on sanctions lists and the long
/// forms the Czech public register writes in addresses ("Spolková republika Německo").
/// Shared by the Czech sanctions list and the Czech register connector. Lookups go
/// through the name normaliser, so case and diacritics do not matter.
/// </summary>
public static class CzCountryNames
{
    private static readonly (string Phrase, string Iso2)[] Entries =
    [
        ("Ruská federace", "RU"), ("Rusko", "RU"),
        ("Ukrajina", "UA"), ("Bělorusko", "BY"), ("Běloruská republika", "BY"),
        ("Gruzie", "GE"), ("Izrael", "IL"), ("Stát Izrael", "IL"),
        ("Česká republika", "CZ"), ("Česko", "CZ"),
        ("Libanonská republika", "LB"), ("Libanon", "LB"),
        ("Slovensko", "SK"), ("Slovenská republika", "SK"), ("Polsko", "PL"), ("Polská republika", "PL"),
        ("Německo", "DE"), ("Spolková republika Německo", "DE"), ("Rakousko", "AT"), ("Rakouská republika", "AT"), ("Maďarsko", "HU"),
        ("Írán", "IR"), ("Íránská islámská republika", "IR"), ("Sýrie", "SY"), ("Syrská arabská republika", "SY"),
        ("Čína", "CN"), ("Čínská lidová republika", "CN"), ("Kazachstán", "KZ"), ("Uzbekistán", "UZ"), ("Kyrgyzstán", "KG"), ("Tádžikistán", "TJ"),
        ("Turecko", "TR"), ("Arménie", "AM"), ("Ázerbájdžán", "AZ"), ("Moldavsko", "MD"), ("Moldavská republika", "MD"),
        ("Litva", "LT"), ("Lotyšsko", "LV"), ("Estonsko", "EE"), ("Kypr", "CY"), ("Kyperská republika", "CY"),
        ("Spojené státy americké", "US"), ("USA", "US"), ("Spojené království", "GB"), ("Velká Británie", "GB"),
        ("Francie", "FR"), ("Francouzská republika", "FR"), ("Itálie", "IT"), ("Italská republika", "IT"), ("Španělsko", "ES"), ("Španělské království", "ES"),
        ("Nizozemsko", "NL"), ("Nizozemské království", "NL"), ("Belgie", "BE"), ("Belgické království", "BE"), ("Švýcarsko", "CH"), ("Švýcarská konfederace", "CH"),
        ("Spojené království Velké Británie a Severního Irska", "GB"), ("Bulharsko", "BG"), ("Bulharská republika", "BG"), ("Rumunsko", "RO"), ("Chorvatsko", "HR"), ("Slovinsko", "SI"),
        ("Litevská republika", "LT"), ("Lotyšská republika", "LV"), ("Estonská republika", "EE"), ("Řecko", "GR"), ("Řecká republika", "GR"), ("Irsko", "IE"), ("Dánsko", "DK"), ("Švédsko", "SE"), ("Finsko", "FI"), ("Norsko", "NO"),
        ("Vietnam", "VN"), ("Vietnamská socialistická republika", "VN"), ("Mongolsko", "MN"), ("Turecká republika", "TR"), ("Kazašská republika", "KZ"), ("Arménská republika", "AM"), ("Ázerbájdžánská republika", "AZ"), ("Uzbecká republika", "UZ"),
        ("Lucembursko", "LU"), ("Srbsko", "RS"), ("Korejská lidově demokratická republika", "KP"), ("Severní Korea", "KP"), ("KLDR", "KP"),
        ("Irák", "IQ"), ("Afghánistán", "AF"), ("Egypt", "EG"), ("Saúdská Arábie", "SA"), ("Spojené arabské emiráty", "AE"), ("Katar", "QA"),
        ("Indie", "IN"), ("Pákistán", "PK"), ("Venezuela", "VE"), ("Kuba", "CU"), ("Myanmar", "MM"), ("Libye", "LY"), ("Jemen", "YE"),
    ];

    private static readonly (string Key, string Phrase, string Iso2)[] ByLength =
        [.. Entries.Select(e => (Key: NameNormalizer.Normalize(e.Phrase).Trim(), e.Phrase, e.Iso2)).OrderByDescending(e => e.Key.Length)];

    private static readonly FrozenDictionary<string, string> Iso2ByKey =
        ByLength.ToFrozenDictionary(e => e.Key, e => e.Iso2, StringComparer.Ordinal);

    /// <summary>
    /// The country named in a phrase, with its qualifiers ignored: "dnes pravděpodobně
    /// Ruská federace" is Russia. Returns the dictionary spelling beside the code.
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

        foreach (var (phraseKey, phrase, iso2) in ByLength)
        {
            if (haystack.Contains(" " + phraseKey + " ", StringComparison.Ordinal))
            {
                return (iso2, phrase);
            }
        }

        return null;
    }
}
