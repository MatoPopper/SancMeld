using System.Globalization;
using System.Text;

namespace SancMeld.Core.Matching;

/// <summary>
/// Canonical name normalisation.
///
/// MUST stay byte-for-byte equivalent to the sm_normalize_name() function created
/// by the InitialSchema migration:
///
///   lower(regexp_replace(unaccent('unaccent', input), '[^[:alnum:]]+', ' ', 'g'))
///
/// If the two drift apart, feature values computed in C# no longer describe the
/// same strings the PostgreSQL trigram index is built on, and scoring silently
/// degrades without any error. NameNormalizerParityTests runs every name in the
/// database, and every codepoint, through both and asserts they agree.
///
/// This is a versioned artefact: changing it invalidates every stored normalised
/// name, the trigram index and every previously recorded score. The version is
/// recorded in match_algorithm.name_norm_version.
/// </summary>
public static class NameNormalizer
{
    /// <summary>
    /// Still 1 after the accent-folding rewrite, deliberately. The version describes the
    /// normal form itself, and that is defined by sm_normalize_name(), which did not
    /// change: the stored column, the index and every recorded score are all still right.
    /// What changed is that C# stopped disagreeing with them. Bumping this would announce
    /// a reindex that nothing needs.
    /// </summary>
    public const short Version = 1;

    public static string Normalize(string? input)
    {
        // The one documented departure from the SQL function, which would answer a single
        // space here: callers test the result for emptiness to mean "nothing to search
        // for", and a name made only of punctuation is exactly that.
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var result = new StringBuilder(input.Length);

        // Codepoints rather than chars: 37 unaccent rules live above the basic plane,
        // where a char is half a surrogate pair and would match no rule at all.
        foreach (var rune in input.EnumerateRunes())
        {
            if (UnaccentRules.ByCodepoint.TryGetValue(rune.Value, out var replacement))
            {
                // A replacement can be several characters (Æ -> AE), none at all (a
                // combining acute), or carry punctuation of its own (¼ -> " 1/4"), so it
                // goes back through the same test the input does.
                foreach (var replacementRune in replacement.EnumerateRunes())
                {
                    Append(result, replacementRune);
                }
            }
            else
            {
                Append(result, rune);
            }
        }

        return result.ToString();
    }

    private static void Append(StringBuilder result, Rune rune)
    {
        if (Rune.IsLetterOrDigit(rune) || IsCombiningMark(rune))
        {
            result.Append(Rune.ToLowerInvariant(rune));
        }
        else if (result.Length == 0 || result[^1] != ' ')
        {
            // One space per run of non-alphanumerics, leading and trailing runs included:
            // that is what '[^[:alnum:]]+' -> ' ' does. Trimming instead would make
            // "»Eliars« Kft." normalise to something the stored value can never equal.
            result.Append(' ');
        }
    }

    /// <summary>
    /// A combining mark that unaccent left alone is part of the word, not a separator.
    ///
    /// PostgreSQL's '[:alnum:]' comes from the database's C library, and glibc counts
    /// combining marks as alphanumeric; .NET's IsLetterOrDigit does not. Without this the
    /// Arabic shadda in دلّا and the Hebrew point in a Russian company name would each be
    /// replaced by a space on one side and kept on the other, splitting one word into two
    /// and changing every trigram around it.
    ///
    /// This only concerns marks unaccent has no rule for. The ones it does know -- every
    /// combining accent over a Latin letter -- were already removed above.
    /// </summary>
    private static bool IsCombiningMark(Rune rune) =>
        Rune.GetUnicodeCategory(rune) is UnicodeCategory.NonSpacingMark
            or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;
}
