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

        var current = confirmed.Where(f => f.Function!.To is null).OrderByDescending(f => f.Function!.From).FirstOrDefault();
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

        var family = findings.FirstOrDefault(f => f.Kind == FindingKind.FamilyMember);
        if (family is not null)
        {
            return (CheckOutcome.FamilyMember, $"Rodinný príslušník politicky exponovanej osoby ({family.RelatedTo}); úroveň dôkazu {family.Level}.");
        }

        var associate = findings.FirstOrDefault(f => f.Kind == FindingKind.CloseAssociate);
        if (associate is not null)
        {
            return (CheckOutcome.CloseAssociate, $"Blízka osoba politicky exponovanej osoby ({associate.RelatedTo}); úroveň dôkazu {associate.Level}.");
        }

        if (findings.Count > 0)
        {
            var persons = findings.Select(f => f.Person.Name).Distinct(StringComparer.Ordinal).Count();
            return (CheckOutcome.Unverified,
                $"Zhoda mena ({persons} {(persons == 1 ? "osoba" : persons < 5 ? "osoby" : "osôb")}) v zdrojoch bez dátumu narodenia; zhodu nemožno potvrdiť ani vylúčiť.");
        }

        var searched = coverage.Where(c => c.LastImportedAt is not null).Select(c => c.Source).ToList();
        return (CheckOutcome.NoMatch, searched.Count > 0
            ? $"Žiadna zhoda v prehľadaných zoznamoch ({string.Join(", ", searched)})."
            : "Žiadna zhoda, ale žiadny zoznam ešte nebol importovaný.");
    }

    private static string Date(DateOnly? date) => date?.ToString("d. M. yyyy", CultureInfo.InvariantCulture) ?? "?";
}
