using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;

namespace PepMeld.Core.Checks;

/// <summary>
/// Stage 1: the PEP lists this instance holds.
///
/// The rule is the one from the analysis. A source that publishes the date of birth
/// confirms a name match (level A) or excludes it as a namesake; a source that does not
/// leaves the match a candidate (level C) and the finding says what would settle it.
/// Each function of a matched person is one finding, ended ones included, with the
/// months since the end, because the person stays exposed for a year after it.
/// </summary>
public sealed class ListsStage(
    PepListMatcher matcher,
    SourceQueryService sources,
    IOptions<PepMeldOptions> options,
    TimeProvider clock) : ICheckStage
{
    public CheckStage Stage => CheckStage.Lists;

    public async Task<StageResult> RunAsync(PepCheck check, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(check);

        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var matches = await matcher.FindAsync(check.Name, check.Country, ct).ConfigureAwait(false);
        var findings = new List<FindingView>();

        foreach (var match in matches)
        {
            EvidenceLevel level;
            bool dateMatched;
            string? toVerify;

            if (match.Person.BirthDate is { } birthDate)
            {
                if (birthDate != check.BirthDate)
                {
                    // A namesake. Not reported: with a date of birth on both sides there is
                    // nothing left to verify, and listing every Novák ever elected would
                    // bury the one finding that matters.
                    continue;
                }

                level = EvidenceLevel.A;
                dateMatched = true;
                toVerify = null;
            }
            else
            {
                level = EvidenceLevel.C;
                dateMatched = false;
                toVerify = "Zdroj nepublikuje dátum narodenia. Overiť v inom zdroji (profil, oznámenie funkcionára, zbierka listín), či ide o tú istú osobu.";
            }

            var quote = Quote(match.Person);

            foreach (var function in match.Functions)
            {
                var months = function.ValidTo is { } to ? MonthsBetween(to, today) : (int?)null;

                // A seat on the board of a state company is its own kind: letter g) is
                // the one category a register, not a parliament, answers.
                findings.Add(new FindingView(
                    function.Category == PepCategory.StateEnterpriseBoard ? FindingKind.BoardSeat : FindingKind.Function,
                    level,
                    new FindingPerson(match.Person.DisplayName, match.Person.BirthDate, dateMatched, match.Person.Residence),
                    new FindingFunction(
                        function.Country,
                        function.Category,
                        function.LegalBasis,
                        LegalBasis.Amlr(function.Category),
                        function.Institution,
                        function.Role,
                        function.ValidFrom,
                        function.ValidTo,
                        function.EndedByDisappearance,
                        function.DateNote),
                    Relation: null,
                    RelatedTo: null,
                    months,
                    [new Evidence(function.SourceUrl ?? match.Person.SourceUrl, match.RetrievedAt, quote)],
                    Explain(match.Source, function, dateMatched, months, options.Value.Matching.FormerPepMonths),
                    toVerify));
            }
        }

        // What was searched, as of now: the answer "nothing found" is only as good as the
        // lists it was looked for in, so they are recorded with the check.
        var coverage = (await sources.ListAsync(ct).ConfigureAwait(false))
            .Select(s => new CoverageView(s.Code, s.Country, s.Name, s.HasBirthDate, s.LastImportedAt, s.Persons))
            .ToList();
        check.CoverageJson = JsonSerializer.Serialize(coverage, PepMeldJson.Options);

        return new StageResult(findings, Note(coverage, check.Country));
    }

    private static string? Quote(PepPerson person)
    {
        var name = string.Join(' ', new[] { person.TitleBefore, person.DisplayName, person.TitleAfter }.Where(s => !string.IsNullOrWhiteSpace(s)));
        return person.BirthDate is { } dob
            ? $"{name}, nar. {dob.ToString("d. M. yyyy", CultureInfo.InvariantCulture)}"
            : name;
    }

    private static string Explain(PepSource source, PepFunction function, bool dateMatched, int? monthsSinceEnd, int formerMonths)
    {
        var who = dateMatched
            ? $"{source.Name} uvádza osobu s rovnakým menom a dátumom narodenia"
            : $"{source.Name} uvádza osobu s rovnakým menom, dátum narodenia zdroj nepublikuje";

        var period = function.ValidTo is { } to
            ? $"od {Date(function.ValidFrom)} do {Date(to)}"
            : $"od {Date(function.ValidFrom)}, trvá";

        var ending = string.Empty;
        if (monthsSinceEnd is { } m && m >= 0)
        {
            ending = function.EndedByDisappearance
                ? $" Funkcia zmizla zo zoznamu pred {Months(m)}; presný koniec zdroj neuvádza."
                : m < formerMonths
                    ? $" Funkcia skončila pred {Months(m)}; zvýšená starostlivosť trvá najmenej {formerMonths} mesiacov po skončení."
                    : $" Funkcia skončila pred {Months(m)}; lehota {formerMonths} mesiacov uplynula, ďalší postup závisí od hodnotenia rizík.";
        }

        var dates = function.DateNote is { Length: > 0 } note ? $" {note}" : string.Empty;
        return $"{who}: {function.Role}, {function.Institution} ({period}).{ending}{dates}";
    }

    private static string Date(DateOnly? date) => date?.ToString("d. M. yyyy", CultureInfo.InvariantCulture) ?? "neznámeho dátumu";

    private static string Months(int months) => months switch
    {
        0 => "menej ako mesiacom",
        1 => "1 mesiacom",
        _ => $"{months} mesiacmi",
    };

    private static string Note(IReadOnlyList<CoverageView> coverage, Country? country)
    {
        var searched = coverage.Where(c => c.LastImportedAt is not null && (country is null || c.Country == country)).Select(c => c.Source).ToList();
        var empty = coverage.Where(c => c.LastImportedAt is null && (country is null || c.Country == country)).Select(c => c.Source).ToList();

        var note = searched.Count > 0
            ? $"Prehľadané zoznamy: {string.Join(", ", searched)}."
            : "Žiadny zoznam ešte nebol importovaný; výsledok nič nevylučuje.";

        if (empty.Count > 0)
        {
            note += $" Bez dát (import ešte nebežal): {string.Join(", ", empty)}.";
        }

        // Honest about the gaps, per source country. These shrink as connectors land;
        // keep the sentence in step with the seeded sources.
        const string cz = "ČR: súdy, ČNB, NKÚ, veľvyslanci, orgány strán, regionálna samospráva; senátori sú bez dátumu narodenia; z firiem so štátnou účasťou len štátne podniky a firmy v zozname CZ_ARES_STATE";
        const string sk = "SR: veľvyslanci, orgány politických strán, sudcovia a prokurátori nižších súdov; verejní funkcionári vrátane orgánov štátnych firiem sú bez dátumu narodenia";

        note += country switch
        {
            Country.CZ => $" V tejto verzii nie sú pokryté ({cz}).",
            Country.SK => $" V tejto verzii nie sú pokryté ({sk}).",
            _ => $" V tejto verzii nie sú pokryté ({sk}; {cz}).",
        };

        return note;
    }

    /// <summary>Whole months from <paramref name="from"/> to <paramref name="to"/>; negative when the end lies ahead.</summary>
    internal static int MonthsBetween(DateOnly from, DateOnly to)
    {
        var months = (to.Year - from.Year) * 12 + to.Month - from.Month;
        if (to.Day < from.Day)
        {
            months--;
        }

        return months;
    }
}
