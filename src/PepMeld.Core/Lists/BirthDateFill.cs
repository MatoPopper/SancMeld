using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PepMeld.Core.Data;

namespace PepMeld.Core.Lists;

/// <summary>What one pass of <see cref="BirthDateFill"/> did.</summary>
/// <param name="Filled">Persons who got a date, or another one, from a lender.</param>
/// <param name="Kept">Persons whose borrowed date still holds.</param>
/// <param name="Withdrawn">Persons whose loan no longer holds: no lender left, or lenders that disagree.</param>
/// <param name="Ambiguous">Names with lenders that disagree on the date; left without one.</param>
/// <param name="Missing">Persons with no date and no lender.</param>
public sealed record BirthDateFillOutcome(int Filled, int Kept, int Withdrawn, IReadOnlyList<string> Ambiguous, int Missing);

/// <summary>
/// Lends a date of birth to a person whose source has none, from another list's record of
/// the same person.
///
/// The same person means the same normalised name and the same office: country, category
/// and role equal, periods overlapping. A name alone would not do, there are two Jan
/// Sobotkas in the Czech lists, a deputy born in 1978 and a senator born in 1961; the senate
/// seat tells them apart. When lenders disagree on the date nothing is lent. Only a
/// source's own date is lent, never one it borrowed itself.
///
/// Measured on production 2026-10-22: of the 76 Czech PEPs in office or out of it for less
/// than two years without a date (all from psp.cz, mostly senators), 57 have a Wikidata
/// record with the same name and a function in that time.
/// </summary>
public sealed class BirthDateFill(PepMeldDbContext db, ILogger<BirthDateFill> logger)
{
    public async Task<BirthDateFillOutcome> RunAsync(CancellationToken ct)
    {
        var borrowers = await db.Persons
            .Include(p => p.Functions)
            .Where(p => p.BirthDate == null || p.BirthDateFrom != null)

            // A date found on the open web and checked against its page is a person's work;
            // a lender would be no better (level B either way) and must not undo it.
            .Where(p => p.BirthDateFrom == null || p.BirthDateFrom != BirthDateResearch.Researched)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var names = borrowers.Select(p => p.NameNormalized).Distinct().ToList();

        var lenders = (await db.Persons.AsNoTracking()
                .Include(p => p.Functions)
                .Include(p => p.Source)
                .Where(p => p.BirthDate != null && p.BirthDateFrom == null && names.Contains(p.NameNormalized))
                .ToListAsync(ct)
                .ConfigureAwait(false))
            .ToLookup(p => p.NameNormalized, StringComparer.Ordinal);

        int filled = 0, kept = 0, withdrawn = 0, missing = 0;
        var ambiguous = new List<string>();

        foreach (var person in borrowers)
        {
            var matches = lenders[person.NameNormalized]
                .Where(l => l.SourceId != person.SourceId && SameOffice(person.Functions, l.Functions))
                .ToList();

            var dates = matches.Select(l => l.BirthDate!.Value).Distinct().ToList();

            if (dates.Count == 1)
            {
                // Of several lenders that agree, the first by code: the same one every run.
                var lender = matches.OrderBy(l => l.Source!.Code, StringComparer.Ordinal).First();

                if (person.BirthDate == dates[0] && person.BirthDateFrom == lender.Source!.Code && person.BirthDateFromUrl == lender.SourceUrl)
                {
                    kept++;
                    continue;
                }

                person.BirthDate = dates[0];
                person.BirthDateFrom = lender.Source!.Code;
                person.BirthDateFromUrl = lender.SourceUrl;
                filled++;
                continue;
            }

            if (dates.Count > 1)
            {
                ambiguous.Add(person.DisplayName);
            }

            if (person.BirthDateFrom is not null)
            {
                person.BirthDate = null;
                person.BirthDateFrom = null;
                person.BirthDateFromUrl = null;
                withdrawn++;
            }
            else if (dates.Count == 0)
            {
                missing++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Birth dates lent: {Filled} new, {Kept} kept, {Withdrawn} withdrawn, {Ambiguous} ambiguous, {Missing} without a lender.",
            filled, kept, withdrawn, ambiguous.Count, missing);

        return new BirthDateFillOutcome(filled, kept, withdrawn, ambiguous, missing);
    }

    /// <summary>One function of each in the same office at the same time.</summary>
    internal static bool SameOffice(IEnumerable<PepFunction> mine, IEnumerable<PepFunction> theirs) =>
        mine.Any(a => theirs.Any(b =>
            a.Country == b.Country
            && a.Category == b.Category
            && string.Equals(a.Role.Trim(), b.Role.Trim(), StringComparison.OrdinalIgnoreCase)
            && (a.ValidFrom ?? DateOnly.MinValue) <= (b.ValidTo ?? DateOnly.MaxValue)
            && (b.ValidFrom ?? DateOnly.MinValue) <= (a.ValidTo ?? DateOnly.MaxValue)));
}
