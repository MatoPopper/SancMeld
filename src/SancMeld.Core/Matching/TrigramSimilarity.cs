using System.Collections.Frozen;

namespace SancMeld.Core.Matching;

/// <summary>
/// pg_trgm's similarity(), in process, for comparing two names that are not both in the
/// database — a listed name against a label fetched from an external register. Same
/// definition as the index the screening runs on, so a score produced here means what a
/// score from the search means: shared trigrams over the union, each word padded with
/// two spaces in front and one behind.
///
/// Takes normalised input (<see cref="NameNormalizer.Normalize"/>): lowercase, letters
/// and digits, single spaces. pg_trgm does its own lowercasing and splitting; feeding
/// it the normal form is what keeps the two in step.
///
/// Checked against the documented value: similarity('word', 'two words') is 0.363636.
/// </summary>
public static class TrigramSimilarity
{
    public static double Similarity(string normalizedA, string normalizedB)
    {
        var a = Trigrams(normalizedA);
        var b = Trigrams(normalizedB);

        if (a.Count == 0 || b.Count == 0)
        {
            return 0;
        }

        var shared = a.Count(b.Contains);
        return shared / (double)(a.Count + b.Count - shared);
    }

    public static FrozenSet<string> Trigrams(string normalized)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);

        foreach (var word in normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var padded = "  " + word + " ";
            for (var i = 0; i + 3 <= padded.Length; i++)
            {
                set.Add(padded.Substring(i, 3));
            }
        }

        return set.ToFrozenSet(StringComparer.Ordinal);
    }
}
