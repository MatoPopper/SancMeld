using PepMeld.Core.Domain;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Checks;

/// <summary>
/// One function, one finding. A prime minister is in the Chamber's data and on
/// Wikidata; a senator in the Chamber's data without a birthday and on Wikidata with one.
/// Shown twice, the better-evidenced row and its echo, the reader has to work out that
/// they are the same office. Here the lesser row is folded into the better one as a
/// second piece of evidence.
///
/// Folded only when there is no doubt it is the same person: both rows carry a date of
/// birth and the dates agree. A row without a date of birth stays on its own, because
/// it may be a namesake, and that is exactly what its level C says.
/// </summary>
public static class FindingMerger
{
    public static IReadOnlyList<FindingView> Merge(IReadOnlyList<FindingView> findings)
    {
        ArgumentNullException.ThrowIfNull(findings);

        var absorbed = new HashSet<int>();
        var result = new List<(int Index, FindingView Finding)>(findings.Count);

        // Best evidence first, so a row is only ever folded into one at least as good.
        var order = findings
            .Select((f, i) => (Finding: f, Index: i))
            .OrderBy(x => x.Finding.Level)
            .ThenByDescending(x => x.Finding.Function?.To is null)
            .ThenByDescending(x => x.Finding.Function?.From)
            .ThenBy(x => x.Index)
            .ToList();

        foreach (var (keeper, index) in order)
        {
            if (absorbed.Contains(index))
            {
                continue;
            }

            var merged = keeper;

            foreach (var (other, otherIndex) in order)
            {
                if (otherIndex == index || absorbed.Contains(otherIndex) || !IsEcho(keeper, other))
                {
                    continue;
                }

                absorbed.Add(otherIndex);
                merged = merged with
                {
                    Evidence = [.. merged.Evidence, .. other.Evidence],
                    Sources = [.. merged.Sources ?? [], .. (other.Sources ?? []).Where(s => !(merged.Sources ?? []).Contains(s, StringComparer.Ordinal))],
                    Explanation = merged.Explanation + $" Tú istú funkciu uvádza aj {string.Join(", ", other.Sources ?? ["ďalší zdroj"])} (úroveň {other.Level}).",
                };
            }

            result.Add((index, merged));
        }

        // Back in the order the stage produced them, with the folded rows gone.
        return result.OrderBy(x => x.Index).Select(x => x.Finding).ToList();
    }

    /// <summary>
    /// Whether <paramref name="other"/> says the same thing about the same person from
    /// another source with evidence no better: same date of birth and name, same country
    /// and category, overlapping or undated terms.
    /// </summary>
    internal static bool IsEcho(FindingView keeper, FindingView other)
    {
        if (keeper.Function is null || other.Function is null || other.Level < keeper.Level)
        {
            return false;
        }

        if (keeper.Sources is null || other.Sources is null || keeper.Sources.Intersect(other.Sources, StringComparer.Ordinal).Any())
        {
            return false;
        }

        if (keeper.Person.DateOfBirth is null || other.Person.DateOfBirth != keeper.Person.DateOfBirth)
        {
            return false;
        }

        if (PersonNames.Normalize(keeper.Person.Name) != PersonNames.Normalize(other.Person.Name))
        {
            return false;
        }

        if (keeper.Function.Country != other.Function.Country || keeper.Function.Category != other.Function.Category)
        {
            return false;
        }

        return Overlap(keeper.Function, other.Function);
    }

    private static bool Overlap(FindingFunction a, FindingFunction b)
    {
        var aFrom = a.From ?? DateOnly.MinValue;
        var aTo = a.To ?? DateOnly.MaxValue;
        var bFrom = b.From ?? DateOnly.MinValue;
        var bTo = b.To ?? DateOnly.MaxValue;
        return aFrom <= bTo && bFrom <= aTo;
    }
}
