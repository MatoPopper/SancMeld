using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Checks;

/// <summary>
/// Stage 3: family members, from the ties research has stored (family_link). Two
/// directions, because the obliged entity needs both:
///
///   the person checked is a relative of a PEP   → FamilyMember, the outcome
///   the person checked is a PEP with relatives  → KnownRelative, for the entity to screen
///
/// A tie found by an agent is level D and says so: it is a lead with a quote and a URL,
/// not a register entry. A relative with a recorded date of birth is matched on it, as a
/// PEP is; one without needs the whole name. A former spouse and a deceased relative are
/// no family members in law and are not reported; a sibling of a Slovak PEP is, but the
/// finding says the Slovak law does not count siblings before 10 July 2027.
/// </summary>
public sealed class FamilyStage(PepMeldDbContext db, IOptions<PepMeldOptions> options) : ICheckStage
{
    public CheckStage Stage => CheckStage.Family;

    public async Task<StageResult> RunAsync(PepCheck check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);

        var query = PersonNames.Normalize(check.Name);
        var threshold = options.Value.Matching.SimilarityThreshold;
        var findings = new List<FindingView>();

        if (query.Length > 0)
        {
            // The table is small (a few ties per PEP researched), so a word-similarity floor
            // in SQL and the exact rules in C# are enough; the GIN index serves the floor.
            var links = await db.FamilyLinks.AsNoTracking()
                .Where(l => l.ReviewStatus != FamilyReviewStatus.Rejected && l.Status != "deceased")
                .Where(l => EF.Functions.TrigramsWordSimilarity(query, l.RelativeNameNormalized) >= 0.5
                         || (l.PepBirthDate == check.BirthDate && EF.Functions.TrigramsWordSimilarity(query, l.PepNameNormalized) >= 0.5))
                .ToListAsync(ct)
                .ConfigureAwait(false);

            foreach (var link in links.Where(l => !IsFormerPartner(l)))
            {
                if (IsThisPerson(query, check.BirthDate, link.RelativeNameNormalized, link.RelativeBirthDate, link.RelativeBirthYear, threshold, out var dateMatched))
                {
                    findings.Add(AsFamilyMember(link, dateMatched));
                }

                if (link.PepBirthDate == check.BirthDate
                    && (PepListMatcher.Contains(query, link.PepNameNormalized) || PepListMatcher.Score(query, link.PepNameNormalized, options.Value.Matching.ContainmentScore) >= threshold))
                {
                    findings.Add(AsKnownRelative(link));
                }
            }
        }

        var researched = await db.FamilyResearchRuns.AsNoTracking()
            .Select(r => new { r.PepName, r.PepBirthDate })
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);

        var withTies = await db.FamilyLinks.AsNoTracking()
            .Select(l => new { l.PepNameNormalized, l.PepBirthDate })
            .Distinct()
            .CountAsync(ct)
            .ConfigureAwait(false);

        var note = withTies == 0
            ? "Rodinné väzby zatiaľ nie sú dohľadané pre žiadnu PEP; etapa nič nevylučuje."
            : $"Rodinné väzby sú známe pre {withTies} PEP: z Wikidaty (úroveň B) a z výskumu agenta vo verejných zdrojoch pre {researched} PEP (úroveň D, kým ich nepotvrdí register alebo človek). Pre ostatné PEP etapa nič nevylučuje.";

        return new StageResult(
            findings
                .OrderBy(f => f.Kind == FindingKind.KnownRelative)
                .ThenBy(f => f.Level)
                .ToList(),
            note);
    }

    /// <summary>
    /// The relative is the person checked: with a recorded date of birth the dates must
    /// agree and every word of the query must be in the name; without one, a given name and
    /// a surname must both be in it word for word, and a recorded year must agree.
    /// </summary>
    internal static bool IsThisPerson(string query, DateOnly checkedBirthDate, string relativeName, DateOnly? relativeBirthDate, int? relativeBirthYear, double threshold, out bool dateMatched)
    {
        dateMatched = false;

        if (relativeBirthDate is { } born)
        {
            if (born != checkedBirthDate)
            {
                return false;
            }

            dateMatched = true;
            return PepListMatcher.Contains(query, relativeName) || PepListMatcher.Score(query, relativeName, 0.9) >= threshold;
        }

        if (relativeBirthYear is { } year && year != checkedBirthDate.Year)
        {
            return false;
        }

        // Exact words, not similarity: a family shares its surname, and its given names
        // differ by a letter as often as not (Eva and Ema Pavlová). A given name and a
        // surname, each word for word.
        return query.Split(' ', StringSplitOptions.RemoveEmptyEntries).Count(w => w.Length > 1) >= 2
            && PepListMatcher.Contains(query, relativeName);
    }

    private static bool IsFormerPartner(FamilyLink link) =>
        link.Status == "former" && link.Relation is FamilyRelation.Spouse or FamilyRelation.Partner;

    private static FindingView AsFamilyMember(FamilyLink link, bool dateMatched)
    {
        var (relevant, law) = Relevance(link);
        var who = $"{RelationLabel(link.Relation)} osoby {link.PepName} (nar. {link.PepBirthDate:d. M. yyyy})";
        var origin = link.Level == EvidenceLevel.B ? "Väzbu uvádza Wikidata (neúradný zdroj)" : "Väzbu dohľadal agent vo verejných zdrojoch";
        var toVerify = dateMatched
            ? $"{origin}; overiť v registri alebo u klienta."
            : $"{origin} a dátum narodenia príbuzného neuvádza; overiť, či ide o tú istú osobu.";

        return new FindingView(
            FindingKind.FamilyMember,
            link.Level,
            new FindingPerson(link.RelativeName, link.RelativeBirthDate, dateMatched, null),
            null,
            link.Relation,
            link.PepName,
            null,
            Evidence(link),
            $"Preverovaná osoba je podľa verejných zdrojov {who}. {law} Väzba naposledy dohľadaná {link.LastFoundAt:d. M. yyyy}.",
            toVerify,
            Sources(link),
            relevant);
    }

    private static FindingView AsKnownRelative(FamilyLink link)
    {
        var (relevant, law) = Relevance(link);
        var born = link.RelativeBirthDate is { } d ? $", nar. {d:d. M. yyyy}" : link.RelativeBirthYear is { } y ? $", nar. {y}" : string.Empty;
        var minor = link.IsMinor ? " (maloletý)" : string.Empty;

        return new FindingView(
            FindingKind.KnownRelative,
            link.Level,
            new FindingPerson(link.RelativeName, link.RelativeBirthDate, false, null),
            null,
            link.Relation,
            link.PepName,
            null,
            Evidence(link),
            $"{RelationLabel(link.Relation)}: {link.RelativeName}{born}{minor}. {law} Naposledy dohľadané {link.LastFoundAt:d. M. yyyy}.",
            link.Level == EvidenceLevel.B ? "Podľa Wikidaty; preveriť aj túto osobu." : "Dohľadané agentom vo verejných zdrojoch; preveriť aj túto osobu.",
            Sources(link),
            relevant);
    }

    /// <summary>
    /// Whether the law of the PEP's country counts this tie. Slovakia (§ 6 ods. 3 zák.
    /// 297/2008): spouse or partner, child and child's spouse, parent; not a sibling.
    /// Czechia (§ 4 odst. 5 písm. b) bod 1 zák. 253/2008 with § 22 OZ, "osoba blízká"):
    /// all of these, siblings included.
    /// </summary>
    internal static (bool Relevant, string Law) Relevance(FamilyLink link) => (link.PepCountry, link.Relation) switch
    {
        (Country.SK, FamilyRelation.Sibling) => (false, "Súrodenec nie je rodinným príslušníkom podľa § 6 ods. 3 zákona č. 297/2008 Z. z.; pri najvyšších funkciách ním bude od 10. 7. 2027 (nariadenie (EÚ) 2024/1624)."),
        (Country.SK, _) => (true, "Rodinný príslušník podľa § 6 ods. 3 zákona č. 297/2008 Z. z."),
        (Country.CZ, _) => (true, "Osoba blízká podle § 4 odst. 5 písm. b) bodu 1 zákona č. 253/2008 Sb."),
        _ => (true, string.Empty),
    };

    private static string RelationLabel(FamilyRelation relation) => relation switch
    {
        FamilyRelation.Spouse => "Manžel/manželka",
        FamilyRelation.Partner => "Partner/partnerka",
        FamilyRelation.Child => "Dieťa",
        FamilyRelation.ChildInLaw => "Zať/nevesta",
        FamilyRelation.Parent => "Rodič",
        FamilyRelation.Sibling => "Súrodenec",
        _ => relation.ToString(),
    };

    /// <summary>Every source the tie's evidence comes from: Wikidata by its URLs, the agent by the rest.</summary>
    private static IReadOnlyList<string> Sources(FamilyLink link)
    {
        var evidence = Evidence(link);
        var sources = new List<string>();

        if (evidence.Any(e => FamilyLinkMerge.IsWikidata(e.Url)))
        {
            sources.Add("FAMILY_WIKIDATA");
        }

        if (evidence.Any(e => !FamilyLinkMerge.IsWikidata(e.Url)))
        {
            sources.Add(link.FoundBy switch
            {
                FamilyFoundBy.Register => "FAMILY_REGISTER",
                FamilyFoundBy.Analyst => "FAMILY_ANALYST",
                _ => "FAMILY_AGENT",
            });
        }

        return sources;
    }

    private static IReadOnlyList<Evidence> Evidence(FamilyLink link) =>
        JsonSerializer.Deserialize<List<Evidence>>(link.EvidenceJson, PepMeldJson.Options) ?? [];
}
