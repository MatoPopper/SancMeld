using System.Collections.Frozen;
using System.Text.RegularExpressions;
using SancMeld.Core.Matching;

namespace PepMeld.Core.Matching;

/// <summary>
/// A query name the way it is compared: without academic titles, through SancMeld's
/// NameNormalizer. The stored names are built from a given and a family name the source
/// keeps apart from the titles, so "Ing. Andrej Babiš" and "Andrej Babiš" have to meet
/// at the same string.
/// </summary>
public static class PersonNames
{
    /// <summary>
    /// Titles as written in Slovak and Czech, without the dot: the normaliser removes the
    /// dot anyway, and a title typed without one ("Ing Novák") should go the same way.
    /// Comparison is against the normalised token, so case and accents do not matter.
    /// </summary>
    private static readonly FrozenSet<string> Titles = new[]
    {
        "ing", "mgr", "bc", "bca", "mga", "judr", "mudr", "mvdr", "phdr", "rndr", "paeddr", "pharmdr",
        "thdr", "thlic", "rsdr", "rcdr", "phmr", "mddr", "ddr", "dr", "prof", "doc", "phd", "csc", "drsc",
        "mba", "llm", "mph", "msc", "dis", "akad", "arch", "emer",
    }.ToFrozenSet(StringComparer.Ordinal);

    /// <summary>
    /// Titles the normaliser would break into two words ("Ph.D." becomes "ph d"), taken
    /// out before it runs. Dropping the leftover single letters afterwards instead would
    /// also drop a genuine initial.
    /// </summary>
    private static readonly Regex CompoundTitles = new(
        @"\b(?:ph\.?\s?d|ll\.?\s?m|th\.?\s?d|dr\.?\s?h\.?\s?c|art\.?\s?d|dis\.?\s?art)\b\.?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>The normal form, titles removed. Empty when nothing but titles and punctuation was given.</summary>
    public static string Normalize(string? name)
    {
        var normalized = NameNormalizer.Normalize(name is null ? null : CompoundTitles.Replace(name, " "));
        if (normalized.Length == 0)
        {
            return string.Empty;
        }

        var words = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Where(w => !Titles.Contains(w));
        return string.Join(' ', words);
    }
}
