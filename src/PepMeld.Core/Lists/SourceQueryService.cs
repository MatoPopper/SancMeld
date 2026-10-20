using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;

namespace PepMeld.Core.Lists;

/// <summary>What is covered and how fresh it is: the first thing a caller should read, and what every check records as its coverage.</summary>
public sealed class SourceQueryService(PepMeldDbContext db)
{
    public async Task<IReadOnlyList<SourceView>> ListAsync(CancellationToken ct)
    {
        var sources = await db.Sources.AsNoTracking().OrderBy(s => s.Country).ThenBy(s => s.Code).ToListAsync(ct).ConfigureAwait(false);
        var result = new List<SourceView>(sources.Count);

        foreach (var source in sources)
        {
            var lastSuccess = await db.ImportRuns.AsNoTracking()
                .Where(r => r.SourceId == source.Id && r.Status == ImportStatus.Success)
                .OrderByDescending(r => r.StartedAt)
                .Select(r => r.FinishedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var lastRun = await db.ImportRuns.AsNoTracking()
                .Where(r => r.SourceId == source.Id)
                .OrderByDescending(r => r.StartedAt)
                .Select(r => new { r.Status, r.Error })
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            var persons = await db.Persons.CountAsync(p => p.SourceId == source.Id, ct).ConfigureAwait(false);
            var open = await db.Functions.CountAsync(f => f.Person!.SourceId == source.Id && f.ValidTo == null, ct).ConfigureAwait(false);

            result.Add(new SourceView(source.Code, source.Country, source.Name, source.Publisher, source.Url, source.HasBirthDate,
                source.Licence, source.IsEnabled, lastSuccess, lastRun?.Status, lastRun?.Error, persons, open));
        }

        return result;
    }
}

public sealed record SourceView(
    string Code,
    Country Country,
    string Name,
    string Publisher,
    string Url,
    bool HasBirthDate,
    string? Licence,
    bool IsEnabled,
    DateTimeOffset? LastImportedAt,
    ImportStatus? LastRunStatus,
    string? LastRunError,
    int Persons,
    int OpenFunctions);
