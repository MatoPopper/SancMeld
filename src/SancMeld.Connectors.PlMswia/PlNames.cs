using System.Text;
using System.Text.RegularExpressions;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// The name cell, which holds the name and everything the publisher wanted to say about
/// it: "BELOV Alexey (BELOV Alexy)", "LIASHCHUK Uladzimir (w zapisie także „LESZCZUK
/// Władimir”, „ЛЕЩУК Владимир” lub „LESHCHUK Vladimir Evgenyevich)", "MEDMIX POLAND Sp.
/// z o.o. (do 3.03.2022 r. – SULZER MIXPAC POLAND Sp. z o.o.)".
///
/// Persons are written FAMILY Given, the family name in capitals — the one structural
/// convention the file keeps, and the one that lets a surname be told from a forename
/// without a dictionary.
/// </summary>
internal static partial class PlNames
{
    public static List<ParsedName> Parse(PlSheet sheet, string cell)
    {
        var (main, parentheticals) = Split(Collapse(cell));
        var names = new List<ParsedName>();

        if (main.Length > 0)
        {
            names.Add(Name(sheet, main, NameType.Primary));
        }

        foreach (var parenthetical in parentheticals)
        {
            var kind = NameType.Alias;
            var text = parenthetical;

            if (PrefixPattern().Match(text) is { Success: true } prefix)
            {
                // "obecnie" is the name the company trades under today and "poprzednio"
                // the one it dropped; both are searched, and the second is marked as what
                // it is. Everything else — another script, another transliteration — is
                // an alias.
                kind = prefix.Groups["former"].Success ? NameType.Former : NameType.Alias;
                text = text[prefix.Length..];
            }

            foreach (var piece in PiecePattern().Split(text))
            {
                var candidate = piece.Trim().Trim('„', '”', '"', '\'', '«', '»', ' ', ',');

                // A second prefix inside one bracket: "(pełna nazwa w języku rosyjskim: X,
                // skrócona nazwa w języku rosyjskim: Y)".
                if (PrefixPattern().Match(candidate) is { Success: true } inner)
                {
                    candidate = candidate[inner.Length..].Trim().Trim('„', '”', '"', '\'', '«', '»', ' ');
                }

                if (candidate.Length == 0 || names.Any(n => string.Equals(n.Value, candidate, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                names.Add(Name(sheet, candidate, IsCyrillic(candidate) ? NameType.NativeScript : kind));
            }
        }

        return names;
    }

    private static ParsedName Name(PlSheet sheet, string value, NameType kind)
    {
        if (sheet != PlSheet.Persons || kind == NameType.NativeScript)
        {
            return new ParsedName { Value = value, Kind = kind };
        }

        var (family, given) = FamilyAndGiven(value);

        return new ParsedName
        {
            Value = value,
            Kind = kind,
            FamilyName = family,
            GivenName = given,
        };
    }

    /// <summary>
    /// Leading tokens written entirely in capitals are the family name; the rest are
    /// given names. "ŞOR Ilan" and "SZNEJDER Andriej Iwanowicz" both follow it. A name
    /// with no lower-case token at all — none today — keeps its first token as the
    /// family name rather than swallowing the whole line into it.
    /// </summary>
    private static (string? Family, string? Given) FamilyAndGiven(string value)
    {
        var tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var familyCount = tokens.TakeWhile(IsCapitals).Count();

        if (familyCount == 0)
        {
            return (null, null);
        }

        if (familyCount == tokens.Length)
        {
            familyCount = 1;
        }

        var family = string.Join(' ', tokens[..familyCount]);
        var given = familyCount == tokens.Length ? null : string.Join(' ', tokens[familyCount..]);

        return (family, given);
    }

    private static bool IsCapitals(string token) =>
        token.Any(char.IsLetter) && !token.Any(char.IsLower);

    private static bool IsCyrillic(string text) =>
        text.Any(c => c is >= 'Ѐ' and <= 'ӿ');

    /// <summary>
    /// The text outside brackets and the text inside each pair, top level only. A bracket
    /// the publisher forgot to close — "(… lub „LESHCHUK Vladimir Evgenyevich" is in the
    /// file — runs to the end of the cell.
    /// </summary>
    private static (string Main, List<string> Parentheticals) Split(string text)
    {
        var main = new StringBuilder();
        var current = new StringBuilder();
        var parentheticals = new List<string>();
        var depth = 0;

        foreach (var c in text)
        {
            switch (c)
            {
                case '(':
                    depth++;
                    if (depth > 1)
                    {
                        current.Append(c);
                    }

                    break;

                case ')' when depth > 0:
                    depth--;
                    if (depth == 0)
                    {
                        parentheticals.Add(current.ToString().Trim());
                        current.Clear();
                    }
                    else
                    {
                        current.Append(c);
                    }

                    break;

                default:
                    (depth > 0 ? current : main).Append(c);
                    break;
            }
        }

        if (current.Length > 0)
        {
            parentheticals.Add(current.ToString().Trim());
        }

        return (main.ToString().Trim().TrimEnd(',', ' '), parentheticals);
    }

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(
        @"^(?:(?<former>poprzednio|dawniej|do\s+\d{1,2}\.\d{1,2}\.\d{4}\s*r\.)|w zapisie także|w języku rosyjskim|w jęz\.\s*rosyjskim|pełna nazwa w języku rosyjskim|skrócona nazwa w języku rosyjskim|nazwa rosyjskojęzyczna|nazwa w języku rosyjskim|inaczej|obecnie)\s*[:–—-]?\s*",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PrefixPattern();

    [GeneratedRegex(@"\s*(?:,|;|\blub\b|\boraz\b)\s*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PiecePattern();
}
