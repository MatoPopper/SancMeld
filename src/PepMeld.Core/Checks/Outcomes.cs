using System.Globalization;
using PepMeld.Core.Domain;

namespace PepMeld.Core.Checks;

/// <summary>
/// One word and one sentence over everything the stages found. The sentence is for a
/// person reading a screen; the findings underneath are what a decision rests on.
/// </summary>
public static class Outcomes
{
    public static (CheckOutcome Outcome, string Summary) Derive(IReadOnlyList<FindingView> findings, IReadOnlyList<CoverageView> coverage, int formerPepMonths)
    {
        ArgumentNullException.ThrowIfNull(findings);
        ArgumentNullException.ThrowIfNull(coverage);

        var confirmed = findings
            .Where(f => f.Kind is FindingKind.Function or FindingKind.BoardSeat && f.Level is EvidenceLevel.A or EvidenceLevel.B && f.Function is not null)
            .ToList();

        // The headline is the most prominent office, not the newest: a prime minister who
        // also took over a ministry last month is a prime minister. Within one category the
        // office held longest leads, for the same reason.
        var current = confirmed
            .Where(f => f.Function!.To is null)
            .OrderBy(f => Prominence(f.Function!.Category))
            .ThenBy(f => f.Function!.From ?? DateOnly.MaxValue)
            .FirstOrDefault();
        if (current is not null)
        {
            var since = current.Function!.From is { } from ? $" (od {Date(from)}, trvá)" : " (trvá)";
            return (CheckOutcome.Pep, $"Politicky exponovaná osoba: {current.Function.Role}, {current.Function.Institution}{since}.");
        }

        var former = confirmed.OrderByDescending(f => f.Function!.To).FirstOrDefault();
        if (former is not null)
        {
            var months = former.MonthsSinceEnd ?? 0;
            var care = months < formerPepMonths
                ? $"do {formerPepMonths} mesiacov od skončenia funkcie, zvýšená starostlivosť trvá"
                : $"lehota {formerPepMonths} mesiacov uplynula, ďalší postup podľa hodnotenia rizík";
            return (CheckOutcome.FormerPep,
                $"Bývalá politicky exponovaná osoba: {former.Function!.Role}, {former.Function.Institution}, skončila {Date(former.Function.To)} ({care}).");
        }

        // A tie the law of the PEP's country does not count (a sibling under Slovak law
        // until 2027) is shown, never the outcome.
        var family = findings.FirstOrDefault(f => f.Kind == FindingKind.FamilyMember && f.LegallyRelevant != false);
        if (family is not null)
        {
            return (CheckOutcome.FamilyMember, $"Rodinný príslušník politicky exponovanej osoby ({family.RelatedTo}); úroveň dôkazu {family.Level}.");
        }

        var associate = findings.FirstOrDefault(f => f.Kind == FindingKind.CloseAssociate);
        if (associate is not null)
        {
            return (CheckOutcome.CloseAssociate, $"Blízka osoba politicky exponovanej osoby ({associate.RelatedTo}); úroveň dôkazu {associate.Level}.");
        }

        // Known relatives of a PEP and ties the law does not count say nothing about the
        // person checked being exposed themselves.
        if (findings.Any(f => f.Kind is FindingKind.Function or FindingKind.BoardSeat))
        {
            var persons = findings.Where(f => f.Kind is FindingKind.Function or FindingKind.BoardSeat).Select(f => f.Person.Name).Distinct(StringComparer.Ordinal).Count();
            return (CheckOutcome.Unverified,
                $"Zhoda mena ({persons} {(persons == 1 ? "osoba" : persons < 5 ? "osoby" : "osôb")}) v zdrojoch bez dátumu narodenia; zhodu nemožno potvrdiť ani vylúčiť.");
        }

        var searched = coverage.Where(c => c.WasSearched).Select(c => c.Source).ToList();
        return (CheckOutcome.NoMatch, searched.Count > 0
            ? $"Žiadna zhoda v prehľadaných zoznamoch ({string.Join(", ", searched)})."
            : "Žiadna zhoda, ale žiadny zoznam ešte nebol importovaný.");
    }

    /// <summary>Lower is more prominent; the order of SK § 6 ods. 2 for the national offices, then the rest.</summary>
    internal static int Prominence(PepCategory category) => category switch
    {
        PepCategory.HeadOfState => 0,
        PepCategory.GovernmentMember => 1,
        PepCategory.MemberOfParliament => 2,
        PepCategory.SeniorJudge => 3,
        PepCategory.CentralBankBoard => 4,
        PepCategory.CourtOfAuditors => 5,
        PepCategory.Prosecutor => 6,
        PepCategory.SeniorOfficer => 7,
        PepCategory.Ambassador => 8,
        PepCategory.EuOrInternational => 9,
        PepCategory.RegionalLeader => 10,
        PepCategory.PartyLeadership => 11,
        PepCategory.StateEnterpriseBoard => 12,
        _ => 13,
    };

    private static string Date(DateOnly? date) => date?.ToString("d. M. yyyy", CultureInfo.InvariantCulture) ?? "?";
}
