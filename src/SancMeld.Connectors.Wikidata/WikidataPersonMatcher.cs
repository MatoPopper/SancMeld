using SancMeld.Core.Matching;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.Wikidata;

/// <summary>A register record as the matcher sees it: its id and every spelling, with the English label kept apart.</summary>
public sealed record RegisterCandidate(string ItemId, string? EnglishLabel, IReadOnlyCollection<string> Labels);

/// <summary>
/// Decides which of the humans born on a given day, if any, is the listed person.
///
/// The birth date has already narrowed the field to a hundred or so; what is left is a
/// name comparison, done with the same trigram similarity the screening runs on, over
/// every listed spelling against every register label in every language. A listed
/// Cyrillic alias meets a Russian label; a Latin transliteration meets the English one.
///
/// Two guards. The score must clear a floor, because a shared surname and a shared
/// birthday is still a coincidence. And the best must clearly beat the second best,
/// because two register records this close are not something a machine should choose
/// between — the person stays unresolved, with a note saying why.
/// </summary>
public static class WikidataPersonMatcher
{
    /// <summary>
    /// What a listed spelling scores against a label that contains all of its words:
    /// "Ramzan Kadyrov" against "Ramzan Akhmadovich Kadyrov". The trigram similarity of
    /// that pair is 0.56, below the floor, and the pair is the same person spelled with
    /// and without a patronymic — the single most common difference between a list and
    /// the register. A containment needs two words; one word is a surname.
    /// </summary>
    public const double ContainmentScore = 0.9;

    public static MatchOutcome Match(
        PendingPerson person,
        IReadOnlyList<RegisterCandidate> candidates,
        double minNameScore,
        double ambiguityMargin)
    {
        var spellings = person.Names
            .Select(n => (Original: n, Normalized: NameNormalizer.Normalize(n).Trim()))
            .Where(n => n.Normalized.Length > 0)
            .ToList();

        if (spellings.Count == 0 || candidates.Count == 0)
        {
            return new MatchOutcome(null, null);
        }

        var ranked = new List<(RegisterCandidate Candidate, double Score, string Name, string Label)>();

        foreach (var candidate in candidates)
        {
            var best = (Score: 0.0, Name: string.Empty, Label: string.Empty);

            foreach (var label in candidate.Labels)
            {
                var normalizedLabel = NameNormalizer.Normalize(label).Trim();
                if (normalizedLabel.Length == 0)
                {
                    continue;
                }

                foreach (var (original, normalized) in spellings)
                {
                    if (!WordsCovered(normalized, normalizedLabel))
                    {
                        continue;
                    }

                    var score = TrigramSimilarity.Similarity(normalized, normalizedLabel);

                    if (Contains(normalized, normalizedLabel))
                    {
                        score = Math.Max(score, ContainmentScore);
                    }

                    if (score > best.Score)
                    {
                        best = (score, original, label);
                    }
                }
            }

            if (best.Score > 0)
            {
                ranked.Add((candidate, best.Score, best.Name, best.Label));
            }
        }

        if (ranked.Count == 0)
        {
            return new MatchOutcome(null,
                $"None of the {candidates.Count} register records born that day shares every word of a listed spelling.");
        }

        ranked.Sort((a, b) => b.Score.CompareTo(a.Score));
        var top = ranked[0];

        if (top.Score < minNameScore)
        {
            return new MatchOutcome(null, $"Best name score {top.Score:0.00} ({top.Candidate.ItemId}) below {minNameScore:0.00}.");
        }

        if (ranked.Count > 1 && top.Score - ranked[1].Score < ambiguityMargin)
        {
            return new MatchOutcome(null,
                $"Ambiguous: {top.Candidate.ItemId} ({top.Score:0.00}) and {ranked[1].Candidate.ItemId} ({ranked[1].Score:0.00}) both match.");
        }

        return new MatchOutcome(
            new IdentityMatch(top.Candidate.ItemId, top.Candidate.EnglishLabel ?? top.Label, top.Name, top.Label, top.Score),
            null);
    }

    /// <summary>
    /// A word of one spelling and a word of the other that are the same word, allowing
    /// for a transliteration's difference: akhmadovitch and akhmadovich.
    /// </summary>
    public const double WordScore = 0.6;

    /// <summary>
    /// Every word of the shorter spelling has a counterpart in the longer one.
    ///
    /// The whole-name similarity cannot be trusted on its own: "Aleksandr Vladimirovich
    /// Korotkiy" and "Aleksandr Vladimirovich Shott" score 0.62 together, because the
    /// shared given name and patronymic are most of both strings — and they are two
    /// men. The surname is the word that carries the identity, and this is what makes
    /// it count: the word with no counterpart fails the pair, however long the rest.
    /// </summary>
    internal static bool WordsCovered(string normalizedA, string normalizedB)
    {
        var a = normalizedA.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = normalizedB.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (shorter, longer) = a.Length <= b.Length ? (a, b) : (b, a);

        return shorter.All(word => longer.Any(other =>
            string.Equals(word, other, StringComparison.Ordinal)
            || TrigramSimilarity.Similarity(word, other) >= WordScore));
    }

    /// <summary>All words of one side occur in the other, and there are at least two of them.</summary>
    internal static bool Contains(string normalizedA, string normalizedB)
    {
        var a = normalizedA.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var b = normalizedB.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var (small, big) = a.Length <= b.Length ? (a, b) : (b, a);

        if (small.Length < 2)
        {
            return false;
        }

        var set = new HashSet<string>(big, StringComparer.Ordinal);
        return small.All(set.Contains);
    }
}

/// <summary>The match, or the reason there is none. A null match with a null note means nothing to compare.</summary>
public sealed record MatchOutcome(IdentityMatch? Match, string? Note);
