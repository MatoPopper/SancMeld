using System.Text;

namespace SancMeld.Core.Matching;

/// <summary>
/// Russian, Ukrainian and Belarusian Cyrillic to Latin, the two ways registers write it.
///
/// A Czech or Slovak company register records a Russian shareholder as their passport
/// spells them: Шнайдер as SHNAYDER (BGN/PCGN, й → y) or SHNAIDER (ICAO 9303, й → i).
/// The sanctions lists carry the Cyrillic and an English spelling that is often neither
/// (SCHNEIDER). Matching the register against the listing's own Cyrillic, transliterated
/// both ways, finds the person word for word instead of relying on a similarity that a
/// surname this short does not reach.
///
/// Not a full transliteration standard: the letters that differ between the two schemes
/// are the ones that matter for matching, and the rest map the same in both.
/// </summary>
public static class CyrillicTransliterator
{
    private static readonly Dictionary<char, (string Icao, string Bgn)> Map = new()
    {
        ['а'] = ("a", "a"),
        ['б'] = ("b", "b"),
        ['в'] = ("v", "v"),
        ['г'] = ("g", "g"),
        ['д'] = ("d", "d"),
        ['е'] = ("e", "e"),
        ['ё'] = ("e", "yo"),
        ['ж'] = ("zh", "zh"),
        ['з'] = ("z", "z"),
        ['и'] = ("i", "i"),
        ['й'] = ("i", "y"),
        ['к'] = ("k", "k"),
        ['л'] = ("l", "l"),
        ['м'] = ("m", "m"),
        ['н'] = ("n", "n"),
        ['о'] = ("o", "o"),
        ['п'] = ("p", "p"),
        ['р'] = ("r", "r"),
        ['с'] = ("s", "s"),
        ['т'] = ("t", "t"),
        ['у'] = ("u", "u"),
        ['ф'] = ("f", "f"),
        ['х'] = ("kh", "kh"),
        ['ц'] = ("ts", "ts"),
        ['ч'] = ("ch", "ch"),
        ['ш'] = ("sh", "sh"),
        ['щ'] = ("shch", "shch"),
        ['ъ'] = ("ie", ""),
        ['ы'] = ("y", "y"),
        ['ь'] = ("", ""),
        ['э'] = ("e", "e"),
        ['ю'] = ("iu", "yu"),
        ['я'] = ("ia", "ya"),
        // Ukrainian and Belarusian letters.
        ['і'] = ("i", "i"),
        ['ї'] = ("i", "yi"),
        ['є'] = ("ie", "ye"),
        ['ґ'] = ("g", "g"),
        ['ў'] = ("u", "w"),
    };

    /// <summary>The ICAO and BGN/PCGN spellings of a Cyrillic text; nothing for a text with no Cyrillic.</summary>
    public static IReadOnlyList<string> Variants(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Any(IsCyrillic))
        {
            return [];
        }

        var icao = new StringBuilder(text.Length + 8);
        var bgn = new StringBuilder(text.Length + 8);

        foreach (var ch in text)
        {
            var lower = char.ToLowerInvariant(ch);
            if (Map.TryGetValue(lower, out var latin))
            {
                icao.Append(latin.Icao);
                bgn.Append(latin.Bgn);
            }
            else
            {
                icao.Append(ch);
                bgn.Append(ch);
            }
        }

        var a = icao.ToString();
        var b = bgn.ToString();
        return a == b ? [a] : [a, b];
    }

    private static bool IsCyrillic(char ch) => ch is >= 'Ѐ' and <= 'ӿ';
}
