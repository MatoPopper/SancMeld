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
/// no family members in law and are not reported. The circle is the wider Czech one for
/// Slovak and Czech PEPs alike; the finding says where it goes beyond the Slovak law.
///
/// A family member is one of a PEP: the tie is the outcome only while its PEP holds a
/// function in the lists or ended it less than the former-PEP period ago. A tie of a PEP
/// who left office longer ago, or whom the lists do not name, is shown for information.
/// </summary>
public sealed class FamilyStage(PepMeldDbContext db, IOptions<PepMeldOptions> options, TimeProvider clock) : ICheckStage
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

            var holders = await HoldersAsync(links, ct).ConfigureAwait(false);
            var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var formerMonths = options.Value.Matching.FormerPepMonths;

            foreach (var link in links.Where(l => !IsFormerPartner(l)))
            {
                if (IsThisPerson(query, check.BirthDate, link.RelativeNameNormalized, link.RelativeBirthDate, link.RelativeBirthYear, threshold, out var dateMatched))
                {
                    findings.Add(AsFamilyMember(link, dateMatched, Standing(link, holders, today, formerMonths)));
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

    /// <summary>A person in the lists who may be a tie's PEP: name, date of birth, page, and the end of each function (null while it lasts).</summary>
    internal sealed record PepHolder(string NameNormalized, DateOnly BirthDate, string SourceUrl, IReadOnlyList<DateOnly?> Ends);

    /// <summary>Everyone in the lists born on a day one of the ties' PEPs was born; few, since a date of birth is rarely shared.</summary>
    private async Task<IReadOnlyList<PepHolder>> HoldersAsync(IReadOnlyList<FamilyLink> links, CancellationToken ct)
    {
        if (links.Count == 0)
        {
            return [];
        }

        var dates = links.Select(l => (DateOnly?)l.PepBirthDate).Distinct().ToList();
        var rows = await db.Persons.AsNoTracking()
            .Where(p => dates.Contains(p.BirthDate))
            .Select(p => new { p.NameNormalized, BirthDate = p.BirthDate!.Value, p.SourceUrl, Ends = p.Functions.Select(f => f.ValidTo).ToList() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return rows.Select(r => new PepHolder(r.NameNormalized, r.BirthDate, r.SourceUrl, r.Ends)).ToList();
    }

    /// <summary>
    /// Whether the tie's PEP makes the relative a family member today. The PEP is found in
    /// the lists by date of birth and either the name (one containing the other: Wikidata
    /// and a register spell the same person with and without a middle name) or the
    /// Wikidata page the tie cites. A function that lasts, or one that ended within the
    /// former-PEP period, counts; the tie of a PEP who left longer ago is information, as
    /// is one whose PEP the lists do not name (Wikidata knows MPs since 1993, the
    /// Slovak parliament's list only the recent terms).
    /// </summary>
    internal static (bool Counts, string Text) Standing(FamilyLink link, IEnumerable<PepHolder> holders, DateOnly today, int formerMonths)
    {
        var urls = Evidence(link).Select(e => e.Url).ToHashSet(StringComparer.Ordinal);
        var ends = holders
            .Where(h => h.BirthDate == link.PepBirthDate
                     && (urls.Contains(h.SourceUrl)
                         || PepListMatcher.Contains(link.PepNameNormalized, h.NameNormalized)
                         || PepListMatcher.Contains(h.NameNormalized, link.PepNameNormalized)))
            .SelectMany(h => h.Ends)
            .ToList();

        if (ends.Count == 0)
        {
            return (false, "PEP sa v importovaných zoznamoch nenašla, preto nemožno overiť, či funkciu zastáva; väzba sa uvádza len na informáciu.");
        }

        if (ends.Any(e => e is null || e >= today))
        {
            return (true, string.Empty);
        }

        var ended = ends.Max()!.Value;
        return ListsStage.MonthsBetween(ended, today) < formerMonths
            ? (true, $"Funkcia PEP skončila {ended:d. M. yyyy}; do {formerMonths} mesiacov od jej skončenia sa väzba posudzuje ako väzba na PEP.")
            : (false, $"Funkcia PEP skončila {ended:d. M. yyyy}, lehota {formerMonths} mesiacov uplynula; väzba sa uvádza len na informáciu, ďalší postup podľa hodnotenia rizík.");
    }

    private static FindingView AsFamilyMember(FamilyLink link, bool dateMatched, (bool Counts, string Text) standing)
    {
        var (lawCounts, law) = Relevance(link);
        var relevant = lawCounts && standing.Counts;
        var pep = standing.Text.Length > 0 ? $" {standing.Text}" : string.Empty;
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
            $"Preverovaná osoba je podľa verejných zdrojov {who}. {law}{pep} Väzba naposledy dohľadaná {link.LastFoundAt:d. M. yyyy}.",
            toVerify,
            Sources(link),
            relevant);
    }

    private static FindingView AsKnownRelative(FamilyLink link)
    {
        var (relevant, law) = Relevance(link);
        var born = link.RelativeBirthDate is { } d ? $", nar. {d:d. M. yyyy}" : link.RelativeBirthYear is { } y ? $", nar. {y}" : string.Empty;

        return new FindingView(
            FindingKind.KnownRelative,
            link.Level,
            new FindingPerson(link.RelativeName, link.RelativeBirthDate, false, null),
            null,
            link.Relation,
            link.PepName,
            null,
            Evidence(link),
            $"{RelationLabel(link.Relation)}: {link.RelativeName}{born}. {law} Naposledy dohľadané {link.LastFoundAt:d. M. yyyy}.",
            link.Level == EvidenceLevel.B ? "Podľa Wikidaty; preveriť aj túto osobu." : "Dohľadané agentom vo verejných zdrojoch; preveriť aj túto osobu.",
            Sources(link),
            relevant);
    }

    /// <summary>
    /// Every tie counts, for Slovak and Czech PEPs alike: PepMeld applies the wider circle of
    /// the two laws, the Czech "osoba blízká", to both (decided 2026-10-20). The text says
    /// which law names the tie, so a Slovak obliged entity sees where it goes beyond its own.
    /// </summary>
    internal static (bool Relevant, string Law) Relevance(FamilyLink link) => link.Relation switch
    {
        FamilyRelation.Spouse or FamilyRelation.Partner or FamilyRelation.Child or FamilyRelation.ChildInLaw or FamilyRelation.Parent =>
            (true, "Rodinný príslušník podľa § 6 ods. 3 zákona č. 297/2008 Z. z. aj osoba blízká podľa § 4 ods. 5 písm. b) bodu 1 zákona č. 253/2008 Sb."),
        FamilyRelation.Sibling =>
            (true, "Osoba blízká podľa § 4 ods. 5 písm. b) bodu 1 zákona č. 253/2008 Sb.; slovenský § 6 ods. 3 súrodenca nemenuje (od 10. 7. 2027 ho pri najvyšších funkciách menuje nariadenie (EÚ) 2024/1624). PepMeld uplatňuje širší český okruh pre obe krajiny."),
        _ =>
            (true, "Osoba blízká podľa § 4 ods. 5 písm. b) bodu 1 zákona č. 253/2008 Sb. v spojení s § 22 občianskeho zákonníka; slovenský § 6 ods. 3 túto väzbu nemenuje. PepMeld uplatňuje širší český okruh pre obe krajiny."),
    };

    private static string RelationLabel(FamilyRelation relation) => relation switch
    {
        FamilyRelation.Spouse => "Manžel/manželka",
        FamilyRelation.Partner => "Partner/partnerka",
        FamilyRelation.Child => "Dieťa",
        FamilyRelation.ChildInLaw => "Zať/nevesta",
        FamilyRelation.Parent => "Rodič",
        FamilyRelation.Sibling => "Súrodenec",
        FamilyRelation.Grandparent => "Starý rodič",
        FamilyRelation.Grandchild => "Vnuk/vnučka",
        FamilyRelation.ParentInLaw => "Svokor/svokra",
        FamilyRelation.SiblingInLaw => "Švagor/švagriná",
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
