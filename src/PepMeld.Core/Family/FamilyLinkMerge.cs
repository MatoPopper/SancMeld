using System.Text.Json;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;

namespace PepMeld.Core.Family;

/// <summary>
/// One tie, whoever found it. An agent writes "Svetlana Ficová (rod. Svobodová)", Wikidata
/// "Svetlana Ficová": the same wife, and she must be one row with the evidence of both, the
/// better level of the two, and the date of birth whichever source had it.
/// </summary>
public static class FamilyLinkMerge
{
    /// <summary>
    /// The existing tie of the same PEP and relation that names this relative: the same
    /// normal form, or one whose words all appear in the other ("svetlana ficova" in
    /// "svetlana ficova rod svobodova"). Two words at least on the shorter side, so a shared
    /// surname alone never merges two children.
    /// </summary>
    public static FamilyLink? FindExisting(IEnumerable<FamilyLink> sameRelation, string relativeNameNormalized)
    {
        ArgumentNullException.ThrowIfNull(sameRelation);

        FamilyLink? partial = null;
        foreach (var link in sameRelation)
        {
            if (link.RelativeNameNormalized == relativeNameNormalized)
            {
                return link;
            }

            if (partial is null && WordsOf(relativeNameNormalized) >= 2 && WordsOf(link.RelativeNameNormalized) >= 2
                && (PepListMatcher.Contains(relativeNameNormalized, link.RelativeNameNormalized) || PepListMatcher.Contains(link.RelativeNameNormalized, relativeNameNormalized)))
            {
                partial = link;
            }
        }

        return partial;
    }

    /// <summary>
    /// The new evidence of one source in place of that source's old evidence, the other
    /// sources' evidence kept. A source is told apart by whether its URLs are on Wikidata.
    /// </summary>
    public static string MergeEvidence(string existingJson, IReadOnlyList<Evidence> incoming, bool incomingIsWikidata)
    {
        var existing = JsonSerializer.Deserialize<List<Evidence>>(existingJson, PepMeldJson.Options) ?? [];
        var kept = existing.Where(e => IsWikidata(e.Url) != incomingIsWikidata);
        return JsonSerializer.Serialize(kept.Concat(incoming).DistinctBy(e => e.Url, StringComparer.Ordinal).ToList(), PepMeldJson.Options);
    }

    /// <summary>The better of two levels: A beats D.</summary>
    public static EvidenceLevel Best(EvidenceLevel a, EvidenceLevel b) => a < b ? a : b;

    public static bool IsWikidata(string url) => url.Contains("wikidata.org/", StringComparison.OrdinalIgnoreCase);

    private static int WordsOf(string normalized) => normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Length > 1);
}
