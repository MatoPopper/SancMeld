using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Family;

/// <summary>A PEP whose family is to be researched next, with what makes them one: the brief an agent works from.</summary>
public sealed record FamilyResearchTask(string Name, DateOnly DateOfBirth, Country Country, IReadOnlyList<string> Functions, DateTimeOffset? LastResearchedAt);

/// <summary>
/// Who is next. One identity per name and date of birth, however many lists name them;
/// only those with a date of birth, because without it an agent cannot tell the PEP from a
/// namesake either. Ordered by what the obliged entity meets first: a current function
/// before an ended one, the head of state before a board member, and among equals the one
/// researched longest ago. A function that ended more than two years ago no longer puts a
/// person in the queue.
///
/// At 100 a day the 4 000 or so identities with a date of birth are covered in about six
/// weeks; with a year's refresh interval each is then looked at again once a year.
/// </summary>
public sealed class FamilyResearchQueue(PepMeldDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<FamilyResearchTask>> NextAsync(int count, int refreshDays, CancellationToken ct)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var horizon = today.AddYears(-2);

        var rows = await db.Functions.AsNoTracking()
            .Where(f => f.Person!.BirthDate != null && f.Person.DeathDate == null && (f.ValidTo == null || f.ValidTo >= horizon))
            .Select(f => new
            {
                f.Person!.DisplayName,
                f.Person.NameNormalized,
                BirthDate = f.Person.BirthDate!.Value,
                f.Country,
                f.Category,
                f.Role,
                f.Institution,
                f.ValidTo,
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var researched = await db.FamilyResearchRuns.AsNoTracking()
            .GroupBy(r => new { r.PepName, r.PepBirthDate })
            .Select(g => new { g.Key.PepName, g.Key.PepBirthDate, Last = g.Max(r => r.FinishedAt) })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var last = researched
            .GroupBy(r => (PersonNames.Normalize(r.PepName), r.PepBirthDate))
            .ToDictionary(g => g.Key, g => g.Max(r => r.Last));

        var cutoff = clock.GetUtcNow().AddDays(-refreshDays);

        return rows
            .GroupBy(r => (r.NameNormalized, r.BirthDate))
            .Select(g =>
            {
                var current = g.Where(r => r.ValidTo is null).ToList();
                var lead = (current.Count > 0 ? current : g.ToList()).OrderBy(r => Outcomes.Prominence(r.Category)).First();
                last.TryGetValue(g.Key, out var lastAt);
                return new
                {
                    Task = new FamilyResearchTask(
                        lead.DisplayName,
                        g.Key.BirthDate,
                        lead.Country,
                        g.OrderBy(r => r.ValidTo is not null).ThenBy(r => Outcomes.Prominence(r.Category))
                            .Select(r => $"{r.Role}, {r.Institution}{(r.ValidTo is { } to ? $" (do {to:yyyy-MM-dd})" : string.Empty)}")
                            .Distinct(StringComparer.Ordinal)
                            .Take(5)
                            .ToList(),
                        lastAt == default ? null : lastAt),
                    Current = current.Count > 0,
                    Prominence = Outcomes.Prominence(lead.Category),
                };
            })
            .Where(x => x.Task.LastResearchedAt is null || x.Task.LastResearchedAt < cutoff)
            .OrderByDescending(x => x.Current)
            .ThenBy(x => x.Prominence)
            .ThenBy(x => x.Task.LastResearchedAt ?? DateTimeOffset.MinValue)
            .ThenBy(x => x.Task.Name, StringComparer.Ordinal)
            .Take(count)
            .Select(x => x.Task)
            .ToList();
    }
}
