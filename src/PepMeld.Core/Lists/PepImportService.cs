using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Matching;

namespace PepMeld.Core.Lists;

/// <summary>
/// Imports one source: download, snapshot, parse, validate, diff against what is stored.
///
/// A diff, never a replace. A function that is in the database and not in the file is
/// closed with today's date and flagged as ended by disappearance; a person is never
/// deleted. That is what the twelve months after an office rest on: the office has to
/// still be there to be counted from.
/// </summary>
public sealed class PepImportService(
    PepMeldDbContext db,
    IEnumerable<IPepListConnector> connectors,
    ISnapshotStore snapshots,
    TimeProvider clock,
    ILogger<PepImportService> logger)
{
    public async Task<ImportOutcome> RunAsync(string sourceCode, bool force, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceCode);

        var source = await db.Sources.FirstOrDefaultAsync(s => s.Code == sourceCode, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"No source with code '{sourceCode}'. Sources are seeded by migrations; see PepMeldDbContext.");

        var connector = connectors.FirstOrDefault(c => c.SourceCode == source.Code)
            ?? throw new InvalidOperationException($"No connector is registered for source '{source.Code}'.");

        var watch = Stopwatch.StartNew();

        // A run still "running" when another starts belongs to a process that died
        // mid-download; nothing of it was applied, and leaving it open would show the
        // source as importing forever.
        await db.ImportRuns
            .Where(r => r.SourceId == source.Id && r.Status == ImportStatus.Running)
            .ExecuteUpdateAsync(s => s
                .SetProperty(r => r.Status, ImportStatus.Failed)
                .SetProperty(r => r.Error, "Interrupted: the process ended before the run did.")
                .SetProperty(r => r.FinishedAt, clock.GetUtcNow()), ct)
            .ConfigureAwait(false);

        var run = new ImportRun { Id = Guid.CreateVersion7(), SourceId = source.Id, StartedAt = clock.GetUtcNow(), Status = ImportStatus.Running };
        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            string path;
            using (var download = await connector.DownloadAsync(ct).ConfigureAwait(false))
            {
                run.SourceUrl = download.Url;
                (path, run.ContentSha256) = await snapshots.SaveAsync(source.Code, download.Content, ct).ConfigureAwait(false);
                run.SnapshotPath = path;
            }

            // The same bytes as the last successful run: nothing to do, and saying so is
            // cheaper than a diff that finds nothing. --force is for a mapping change.
            var lastHash = await db.ImportRuns
                .Where(r => r.SourceId == source.Id && r.Status == ImportStatus.Success && r.Id != run.Id)
                .OrderByDescending(r => r.StartedAt)
                .Select(r => r.ContentSha256)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);

            if (!force && lastHash is not null && lastHash == run.ContentSha256)
            {
                return await FinishAsync(run, source, ImportStatus.SkippedUnchanged, "The file is unchanged since the last import.", watch, ct).ConfigureAwait(false);
            }

            IReadOnlyList<ParsedPerson> parsed;
            await using (var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                parsed = connector.Parse(file);
            }

            run.PersonsSeen = parsed.Count;
            run.FunctionsSeen = parsed.Sum(p => p.Functions.Count);

            if (parsed.Count < source.MinExpectedPersons)
            {
                return await FinishAsync(run, source, ImportStatus.Failed,
                    $"Only {parsed.Count} persons parsed; at least {source.MinExpectedPersons} expected. Refusing to apply what looks like a truncated file.",
                    watch, ct).ConfigureAwait(false);
            }

            await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
            Apply(source, run, parsed, await LoadExistingAsync(source, ct).ConfigureAwait(false));
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            await transaction.CommitAsync(ct).ConfigureAwait(false);

            return await FinishAsync(run, source, ImportStatus.Success, null, watch, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Import of {Source} failed.", source.Code);

            if (ex is DbUpdateConcurrencyException concurrency)
            {
                // Which row the database said was not there: the one fact that explains
                // this exception, and the one it does not put in its message.
                foreach (var entry in concurrency.Entries)
                {
                    logger.LogError("Not in the database as expected: {Entity} {State} {Key}",
                        entry.Metadata.ClrType.Name, entry.State,
                        string.Join(", ", entry.Properties.Where(p => p.Metadata.IsPrimaryKey()).Select(p => p.CurrentValue)));
                }
            }

            db.ChangeTracker.Clear();
            db.ImportRuns.Attach(run);
            return await FinishAsync(run, source, ImportStatus.Failed, ex.Message, watch, ct).ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, PepPerson>> LoadExistingAsync(PepSource source, CancellationToken ct) =>
        await db.Persons
            .Include(p => p.Functions)
            .Where(p => p.SourceId == source.Id)
            .ToDictionaryAsync(p => p.SourceKey, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

    private void Apply(PepSource source, ImportRun run, IReadOnlyList<ParsedPerson> parsed, Dictionary<string, PepPerson> existing)
    {
        var today = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var seenPersons = new HashSet<string>(StringComparer.Ordinal);

        foreach (var item in parsed)
        {
            // A date another list lent stays until this source publishes one of its own;
            // BirthDateFill decides whether the loan still holds.
            var borrowed = existing.TryGetValue(item.SourceKey, out var known) && known.BirthDateFrom is not null && item.BirthDate is null;
            var birthDate = borrowed ? known!.BirthDate : item.BirthDate;

            if (!seenPersons.Add(item.SourceKey))
            {
                // Two records under one key would violate the unique index; the first wins,
                // and the connector is where the duplicate should be fixed.
                logger.LogWarning("{Source}: duplicate person key {Key} in the file; keeping the first.", source.Code, item.SourceKey);
                continue;
            }

            if (!existing.TryGetValue(item.SourceKey, out var person))
            {
                person = new PepPerson
                {
                    Id = Guid.CreateVersion7(),
                    SourceId = source.Id,
                    SourceKey = item.SourceKey,
                    DisplayName = item.DisplayName,
                    NameNormalized = PersonNames.Normalize(item.DisplayName),
                    SourceUrl = item.SourceUrl,
                    FirstSeenRunId = run.Id,
                    LastSeenRunId = run.Id,
                };
                db.Persons.Add(person);
                run.PersonsInserted++;
            }
            else
            {
                var changed = person.DisplayName != item.DisplayName
                    || person.GivenName != item.GivenName
                    || person.FamilyName != item.FamilyName
                    || person.TitleBefore != item.TitleBefore
                    || person.TitleAfter != item.TitleAfter
                    || person.BirthDate != birthDate
                    || person.DeathDate != item.DeathDate
                    || person.Residence != item.Residence
                    || person.SourceUrl != item.SourceUrl;

                if (changed)
                {
                    run.PersonsUpdated++;
                }

                person.LastSeenRunId = run.Id;
            }

            person.DisplayName = item.DisplayName;
            person.NameNormalized = PersonNames.Normalize(item.DisplayName);
            person.GivenName = item.GivenName;
            person.FamilyName = item.FamilyName;
            person.TitleBefore = item.TitleBefore;
            person.TitleAfter = item.TitleAfter;
            person.BirthDate = birthDate;
            if (!borrowed)
            {
                person.BirthDateFrom = null;
                person.BirthDateFromUrl = null;
            }

            person.DeathDate = item.DeathDate;
            person.Residence = item.Residence;
            person.SourceUrl = item.SourceUrl;

            var functions = person.Functions.ToDictionary(f => f.SourceKey, StringComparer.Ordinal);
            var seenFunctions = new HashSet<string>(StringComparer.Ordinal);

            foreach (var pf in item.Functions)
            {
                if (!seenFunctions.Add(pf.SourceKey))
                {
                    continue;
                }

                if (!functions.TryGetValue(pf.SourceKey, out var function))
                {
                    function = new PepFunction
                    {
                        Id = Guid.CreateVersion7(),
                        PersonId = person.Id,
                        SourceKey = pf.SourceKey,
                        LegalBasis = Domain.LegalBasis.National(pf.Country, pf.Category),
                        Institution = pf.Institution,
                        Role = pf.Role,
                        FirstSeenRunId = run.Id,
                        LastSeenRunId = run.Id,
                    };

                    // Added to the set, not only to the person's collection: a new entity
                    // with a key of its own that is merely discovered hanging off an
                    // unchanged person is tracked as modified, and its UPDATE then finds
                    // no row. Fix-up puts it in the collection as well.
                    db.Functions.Add(function);
                    run.FunctionsInserted++;
                }
                else
                {
                    var changed = function.Institution != pf.Institution
                        || function.Role != pf.Role
                        || function.ValidFrom != pf.From
                        || function.ValidTo != pf.To
                        || function.Category != pf.Category
                        || function.DateNote != pf.DateNote
                        || function.EndedByDisappearance;

                    if (changed)
                    {
                        run.FunctionsUpdated++;
                    }

                    function.LastSeenRunId = run.Id;
                }

                function.Country = pf.Country;
                function.Category = pf.Category;
                function.LegalBasis = Domain.LegalBasis.National(pf.Country, pf.Category);
                function.Institution = pf.Institution;
                function.Role = pf.Role;
                function.ValidFrom = pf.From;
                function.ValidTo = pf.To;
                function.EndedByDisappearance = false;
                function.DateNote = pf.DateNote;
                function.SourceUrl = pf.SourceUrl;
            }

            foreach (var gone in person.Functions.Where(f => !seenFunctions.Contains(f.SourceKey) && f.ValidTo is null))
            {
                gone.ValidTo = today;
                gone.EndedByDisappearance = true;
                run.FunctionsEnded++;
            }
        }

        // Persons the file no longer carries at all: their open functions end today too.
        foreach (var person in existing.Values.Where(p => !seenPersons.Contains(p.SourceKey)))
        {
            foreach (var gone in person.Functions.Where(f => f.ValidTo is null))
            {
                gone.ValidTo = today;
                gone.EndedByDisappearance = true;
                run.FunctionsEnded++;
            }
        }
    }

    private async Task<ImportOutcome> FinishAsync(ImportRun run, PepSource source, ImportStatus status, string? reason, Stopwatch watch, CancellationToken ct)
    {
        run.Status = status;
        run.Error = status == ImportStatus.Failed ? reason : null;
        run.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Import of {Source}: {Status} in {Seconds:N1}s; seen {Seen}, new {Inserted}, changed {Updated}, functions ended {Ended}. {Reason}",
            source.Code, status, watch.Elapsed.TotalSeconds, run.PersonsSeen, run.PersonsInserted, run.PersonsUpdated, run.FunctionsEnded, reason);

        return new ImportOutcome(source.Code, status, watch.Elapsed,
            run.PersonsSeen, run.PersonsInserted, run.PersonsUpdated,
            run.FunctionsSeen, run.FunctionsInserted, run.FunctionsUpdated, run.FunctionsEnded, reason);
    }
}
