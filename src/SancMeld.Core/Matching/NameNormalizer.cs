using System.Globalization;
using System.Text;

namespace SancMeld.Core.Matching;

/// <summary>
/// Canonical name normalisation.
///
/// MUST stay byte-for-byte equivalent to the sm_normalize_name() function created
/// by the InitialSchema migration.
/// If the two drift apart, feature values computed in C# no longer describe the
/// same strings the PostgreSQL trigram index is built on, and scoring silently
/// degrades without any error.
///
/// This is a versioned artefact: changing it invalidates every stored normalised
/// name, the trigram index and every previously recorded score. The version is
/// recorded in match_algorithm.name_norm_version.
/// </summary>
public static class NameNormalizer
{
    public const short Version = 1;

    public static string Normalize(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var decomposed = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(decomposed.Length);

        foreach (var ch in decomposed)
        {
            // Drop combining marks -> the unaccent() half of the SQL function.
            if (CharUnicodeInfo.GetUnicodeCategory(ch) == UnicodeCategory.NonSpacingMark) continue;
            sb.Append(char.IsLetterOrDigit(ch) ? char.ToLowerInvariant(ch) : ' ');
        }

        return string.Join(' ', sb.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }
}
