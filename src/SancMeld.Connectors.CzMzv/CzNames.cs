using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// Names come as every spelling the ministry knows, slash-separated, in two cells:
/// "JEVTUŠENKO/JEVTUŠENKOV/YEVTUSHENKO/EVTUSHENKOV/ЕВТУШЕНКОВ" and "Vladimír
/// Petrovič/Vladimir Petrovich/Владимир Петрович". The lists are not parallel — five
/// surnames against three given names is common — so a spelling is paired with one
/// in the same script, and the Cyrillic surname never ends up beside a Czech given
/// name.
/// </summary>
internal static class CzNames
{
    private enum Script { Latin, Other }

    public static List<ParsedName> Parse(bool isPerson, string surnameCell, string givenCell)
    {
        var surnames = Variants(surnameCell);

        if (!isPerson)
        {
            return [.. surnames.Select((name, index) => new ParsedName
            {
                Value = name,
                Kind = index == 0 ? NameType.Primary : ScriptOf(name) == Script.Latin ? NameType.Alias : NameType.NativeScript,
            })];
        }

        var givens = Variants(givenCell);
        var names = new List<ParsedName>();
        var usedSurnames = new HashSet<int>();

        // Every given name with a surname of its own script: the n-th Latin given name
        // takes the n-th Latin surname, and when the surnames run out, the last one of
        // that script. A given name in a script no surname has falls back to the first.
        for (var i = 0; i < givens.Count; i++)
        {
            var script = ScriptOf(givens[i]);
            var ordinal = givens.Take(i).Count(g => ScriptOf(g) == script);
            var candidates = surnames.Select((s, index) => (s, index)).Where(x => ScriptOf(x.s) == script).ToList();

            var (surname, index) = candidates.Count == 0
                ? (surnames[0], 0)
                : candidates[Math.Min(ordinal, candidates.Count - 1)];

            usedSurnames.Add(index);
            Add(names, givens[i], surname);
        }

        // Surname spellings nothing was paired with still need to be searchable, beside
        // the first given name of their script.
        for (var index = 0; index < surnames.Count; index++)
        {
            if (usedSurnames.Contains(index))
            {
                continue;
            }

            var script = ScriptOf(surnames[index]);
            var given = givens.FirstOrDefault(g => ScriptOf(g) == script) ?? givens[0];
            Add(names, given, surnames[index]);
        }

        return names;
    }

    private static void Add(List<ParsedName> names, string given, string surname)
    {
        var value = given + " " + surname;

        if (names.Any(n => string.Equals(n.Value, value, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        names.Add(new ParsedName
        {
            Value = value,
            Kind = names.Count == 0 ? NameType.Primary : ScriptOf(value) == Script.Latin ? NameType.Alias : NameType.NativeScript,
            GivenName = given,
            FamilyName = surname,
        });
    }

    /// <summary>Slashes separate spellings; the spaces around them are inconsistent.</summary>
    private static List<string> Variants(string cell) =>
        [.. cell.Split('/')
            .Select(part => string.Join(' ', part.Split((char[])[' ', '\t', ' '], StringSplitOptions.RemoveEmptyEntries)))
            .Where(part => part.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Latin covers the diacritics Czech and Polish transliterations use; anything else
    /// — Cyrillic, Georgian, Arabic in this list — is the subject's own script.
    /// </summary>
    private static Script ScriptOf(string text) =>
        text.Any(c => char.IsLetter(c) && c > 'ɏ') ? Script.Other : Script.Latin;
}
