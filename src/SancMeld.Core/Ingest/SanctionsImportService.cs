using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;

namespace SancMeld.Core.Ingest;

/// <summary>
/// Runs one source through <see cref="IngestPhase"/> from end to end.
///
/// The property everything else is arranged around: nothing a screening query can see is
/// touched until the dataset has passed its thresholds. Discovery, download, snapshot and
/// staging all happen first and are all reversible; only the last transaction publishes.
/// A run that fails anywhere leaves the previous list live and intact.
/// </summary>
public sealed class SanctionsImportService(
    SancMeldDbContext db,
    IEnumerable<ISanctionsConnector> connectors,
    ISnapshotStore snapshots,
    TimeProvider clock,
    ILogger<SanctionsImportService> logger)
{
    private const int StagingBatchSize = 500;
    private const int CommitBatchSize = 250;

    public Task<ImportOutcome> RunAsync(string sourceCode, CancellationToken ct) =>
        RunAsync(sourceCode, force: false, ct);

    /// <summary>
    /// Runs an import. With <c>force</c>, re-processes the current file even when nothing
    /// about it has changed.
    ///
    /// Forcing is for when the mapping changed rather than the data: a new field, a
    /// corrected parse. The publisher is still serving the same bytes, so an ordinary run
    /// correctly decides there is nothing to do — and the fix never lands. Without this,
    /// shipping a parser change means waiting for the EU to republish.
    /// </summary>
    public async Task<ImportOutcome> RunAsync(string sourceCode, bool force, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var connector = connectors.FirstOrDefault(c => c.SourceCode == sourceCode)
            ?? throw new InvalidOperationException($"No connector is registered for source '{sourceCode}'.");

        var source = await db.Sources.FirstOrDefaultAsync(s => s.Code == sourceCode, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"Source '{sourceCode}' is not in sanctions_source. Run the migrations, which seed the known sources.");

        var run = new ImportRun
        {
            Id = Guid.CreateVersion7(),
            SourceId = source.Id,
            StartedAt = clock.GetUtcNow(),
            Status = "RUNNING",

            // Which build produced these rows. Without it, a mapping bug found in six
            // months cannot be traced to the revisions it affected.
            ParserVersion = connector.GetType().Assembly.GetName().Version?.ToString(),
        };

        db.ImportRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            return await ExecuteAsync(connector, source, run, started, force, ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Import of {SourceCode} failed in run {RunId}.", sourceCode, run.Id);

            await FinishAsync(run, "FAILED", ex.Message, ct).ConfigureAwait(false);

            return new ImportOutcome
            {
                ImportRunId = run.Id,
                SourceCode = sourceCode,
                Status = ImportStatus.Failed,
                Reason = ex.Message,
                Duration = started.Elapsed,
            };
        }
    }

    private async Task<ImportOutcome> ExecuteAsync(
        ISanctionsConnector connector,
        SanctionsSource source,
        ImportRun run,
        Stopwatch started,
        bool force,
        CancellationToken ct)
    {
        // --- DiscoverEndpoint -------------------------------------------------
        var discovery = await connector.DiscoverAsync(ct).ConfigureAwait(false);
        var conditional = force
            ? new ConditionalRequestState(null, null, null)
            : await BuildConditionalStateAsync(source.Id, ct).ConfigureAwait(false);

        if (force)
        {
            logger.LogInformation("{SourceCode}: forced run, re-processing whatever the publisher serves.", source.Code);
        }

        logger.LogInformation(
            "{SourceCode}: discovered version {Version} at {Url}.",
            source.Code, discovery.ExternalVersion ?? "(unknown)", discovery.DownloadUrl);

        // --- Download ---------------------------------------------------------
        var download = await connector.DownloadAsync(discovery, conditional, ct).ConfigureAwait(false);

        await using (download.Content)
        {
            if (download.Unchanged)
            {
                logger.LogInformation("{SourceCode}: already holding this release, nothing downloaded.", source.Code);
                await FinishAsync(run, "SKIPPED_UNCHANGED", null, ct).ConfigureAwait(false);

                return Outcome(run, source, ImportStatus.SkippedUnchanged, started, reason: "Publisher version unchanged");
            }

            run.HttpStatus = download.HttpStatus;
            run.ETag = download.ETag;
            run.LastModified = download.LastModified;
            run.DownloadSha256 = download.Sha256;
            run.DownloadBytes = download.ByteSize;

            // --- ValidateTransport --------------------------------------------
            // A login page or an outage notice is served with status 200 and parses as
            // "zero records", which the dataset guards would then have to catch. Catching
            // it here means the snapshot is never even written.
            if (download.HttpStatus != 200 || download.ByteSize == 0)
            {
                throw new InvalidOperationException(
                    $"Transport check failed: HTTP {download.HttpStatus}, {download.ByteSize} bytes.");
            }

            // The publisher moved its version marker but served the same bytes. Whatever
            // changed, it was not the data, and re-parsing 25 MB to discover that every
            // record is unchanged helps nobody.
            if (string.Equals(conditional.PreviousSha256, download.Sha256, StringComparison.OrdinalIgnoreCase))
            {
                logger.LogInformation(
                    "{SourceCode}: release {Version} is byte-identical to the one already imported.",
                    source.Code, discovery.ExternalVersion ?? "(unknown)");

                await FinishAsync(run, "SKIPPED_UNCHANGED", null, ct).ConfigureAwait(false);

                return Outcome(run, source, ImportStatus.SkippedUnchanged, started, "Downloaded bytes unchanged");
            }

            // --- StoreSnapshot -------------------------------------------------
            var storageUri = await snapshots
                .SaveAsync(source.Code, download.Sha256, download.Content, ct)
                .ConfigureAwait(false);

            // A snapshot is one artefact, not one fetch: the unique index on
            // (source_id, sha256) says so. A publisher that reverts to an earlier release
            // must reuse that release's row rather than store the same bytes twice.
            var snapshot = await db.Snapshots
                .FirstOrDefaultAsync(s => s.SourceId == source.Id && s.Sha256 == download.Sha256, ct)
                .ConfigureAwait(false);

            if (snapshot is null)
            {
                snapshot = new SourceSnapshot
                {
                    Id = Guid.CreateVersion7(),
                    SourceId = source.Id,
                    ImportRunId = run.Id,
                    RetrievedAt = clock.GetUtcNow(),
                    ExternalVersion = discovery.ExternalVersion,
                    Sha256 = download.Sha256,
                    ByteSize = download.ByteSize,
                    ContentType = download.ContentType,
                    StorageUri = storageUri,
                    HttpHeadersJson = discovery.Headers is null
                        ? null
                        : System.Text.Json.JsonSerializer.Serialize(discovery.Headers),
                };

                db.Snapshots.Add(snapshot);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }

            // --- Parse + Stage --------------------------------------------------
            download.Content.Position = 0;
            var metadata = new SourceSnapshotMetadata(
                snapshot.Id, source.Code, download.Sha256, discovery.ExternalVersion, snapshot.RetrievedAt);

            var staged = await StageAsync(connector, metadata, download.Content, run, ct).ConfigureAwait(false);

            // --- ValidateDataset -------------------------------------------------
            var previousCount = await db.Designations
                .CountAsync(d => d.SourceId == source.Id && d.ValidTo == null, ct)
                .ConfigureAwait(false);

            var validation = await connector
                .ValidateAsync(new ParsedDatasetStatistics(staged.Accepted, staged.Rejected, previousCount == 0 ? null : previousCount), ct)
                .ConfigureAwait(false);

            if (!validation.IsAcceptable)
            {
                logger.LogWarning("{SourceCode}: dataset rejected. {Reason}", source.Code, validation.Reason);

                await ClearStagingAsync(run.Id, ct).ConfigureAwait(false);
                run.RecordsSeen = staged.Accepted;
                await FinishAsync(run, "REJECTED_VALIDATION", validation.Reason, ct).ConfigureAwait(false);

                return Outcome(run, source, ImportStatus.RejectedValidation, started, validation.Reason);
            }

            // --- Stage → Diff → Commit -------------------------------------------
            var committed = await CommitAsync(source, run, snapshot.Id, ct).ConfigureAwait(false);

            await ClearStagingAsync(run.Id, ct).ConfigureAwait(false);

            run.RecordsSeen = staged.Accepted;
            run.RecordsInserted = committed.Inserted;
            run.RecordsUpdated = committed.Updated;
            run.RecordsUnchanged = committed.Unchanged;
            run.RecordsDelisted = committed.MarkedPossiblyMissing;
            run.WarningsCount = staged.Rejected;
            await FinishAsync(run, "SUCCESS", null, ct).ConfigureAwait(false);

            logger.LogInformation(
                "{SourceCode}: {Seen} records — {Inserted} new, {Updated} changed, {Unchanged} unchanged, {Missing} now possibly missing.",
                source.Code, staged.Accepted, committed.Inserted, committed.Updated,
                committed.Unchanged, committed.MarkedPossiblyMissing);

            return committed with
            {
                ImportRunId = run.Id,
                SourceCode = source.Code,
                Status = ImportStatus.Success,
                RecordsSeen = staged.Accepted,
                Duration = started.Elapsed,
            };
        }
    }

    private async Task<ConditionalRequestState> BuildConditionalStateAsync(Guid sourceId, CancellationToken ct)
    {
        var previous = await db.ImportRuns
            .Where(r => r.SourceId == sourceId && r.Status == "SUCCESS")
            .OrderByDescending(r => r.StartedAt)
            .Select(r => new { r.Id, r.ETag, r.LastModified, r.DownloadSha256 })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (previous is null)
        {
            return new ConditionalRequestState(null, null, null);
        }

        var externalVersion = await db.Snapshots
            .Where(s => s.ImportRunId == previous.Id)
            .Select(s => s.ExternalVersion)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new ConditionalRequestState(previous.ETag, previous.LastModified, previous.DownloadSha256)
        {
            PreviousExternalVersion = externalVersion,
        };
    }

    private async Task<(int Accepted, int Rejected)> StageAsync(
        ISanctionsConnector connector,
        SourceSnapshotMetadata metadata,
        Stream content,
        ImportRun run,
        CancellationToken ct)
    {
        var batch = new List<ImportStagingRecord>(StagingBatchSize);
        var keys = new HashSet<string>(StringComparer.Ordinal);
        var accepted = 0;
        var duplicates = 0;

        db.ChangeTracker.AutoDetectChangesEnabled = false;

        try
        {
            await foreach (var parsed in connector.ParseAsync(content, metadata, ct).ConfigureAwait(false))
            {
                // A publisher repeating a key in one file is a defect in that file, not a
                // second subject. Taking the first and counting the rest keeps the import
                // running while making the defect visible on the run.
                if (!keys.Add(parsed.SourceRecordKey))
                {
                    duplicates++;
                    continue;
                }

                batch.Add(new ImportStagingRecord
                {
                    ImportRunId = run.Id,
                    SourceRecordKey = parsed.SourceRecordKey,
                    ContentSha256 = DesignationPayload.ContentHash(parsed),
                    Payload = DesignationPayload.Serialize(parsed),
                });

                accepted++;

                if (batch.Count >= StagingBatchSize)
                {
                    await FlushAsync(batch, ct).ConfigureAwait(false);
                }
            }

            await FlushAsync(batch, ct).ConfigureAwait(false);
        }
        finally
        {
            db.ChangeTracker.AutoDetectChangesEnabled = true;
        }

        if (duplicates > 0)
        {
            logger.LogWarning(
                "{SourceCode}: {Count} records repeated a key already seen in the same file and were ignored.",
                metadata.SourceCode, duplicates);
        }

        return (accepted, duplicates);
    }

    private async Task FlushAsync(List<ImportStagingRecord> batch, CancellationToken ct)
    {
        if (batch.Count == 0)
        {
            return;
        }

        db.ImportStaging.AddRange(batch);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.ChangeTracker.Clear();
        batch.Clear();
    }

    /// <summary>
    /// Diff and publish, in one transaction. Everything a screening query can see changes
    /// here or not at all.
    /// </summary>
    private async Task<ImportOutcome> CommitAsync(
        SanctionsSource source,
        ImportRun run,
        Guid snapshotId,
        CancellationToken ct)
    {
        var now = clock.GetUtcNow();

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var current = await db.Designations
            .Where(d => d.SourceId == source.Id && d.ValidTo == null)
            .Select(d => new CurrentRevision(d.Id, d.SourceRecordKey, d.ContentSha256, d.Status, d.FirstSeenRunId))
            .ToDictionaryAsync(d => d.SourceRecordKey, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

        var programmes = await LoadProgrammesAsync(source.Id, ct).ConfigureAwait(false);
        var legalInstruments = await LoadLegalInstrumentsAsync(source.JurisdictionId, ct).ConfigureAwait(false);

        var seen = new HashSet<string>(current.Count, StringComparer.Ordinal);
        var unchangedIds = new List<Guid>();
        var reappeared = new List<Guid>();
        var supersededIds = new List<Guid>();
        int inserted = 0, updated = 0;

        var offset = 0;
        while (true)
        {
            var page = await db.ImportStaging
                .AsNoTracking()
                .Where(s => s.ImportRunId == run.Id)
                .OrderBy(s => s.SourceRecordKey)
                .Skip(offset)
                .Take(CommitBatchSize)
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {
                break;
            }

            offset += page.Count;
            var revisions = new List<Designation>(page.Count);

            foreach (var staging in page)
            {
                seen.Add(staging.SourceRecordKey);

                if (current.TryGetValue(staging.SourceRecordKey, out var existing))
                {
                    if (string.Equals(existing.ContentSha256, staging.ContentSha256, StringComparison.Ordinal))
                    {
                        unchangedIds.Add(existing.Id);

                        // It was absent from an earlier release and is back. The content
                        // hash cannot notice, because our own PossiblyMissing marking is
                        // not part of the publisher's content.
                        if (existing.Status == DesignationStatus.PossiblyMissing)
                        {
                            reappeared.Add(existing.Id);
                        }

                        continue;
                    }

                    supersededIds.Add(existing.Id);
                    updated++;
                }
                else
                {
                    inserted++;
                }

                var parsed = DesignationPayload.Deserialize(staging.Payload);
                var revision = DesignationMapper.ToRevision(
                    parsed,
                    source.Id,
                    snapshotId,
                    firstSeenRunId: existing?.FirstSeenRunId ?? run.Id,
                    lastSeenRunId: run.Id,
                    validFrom: now,
                    contentSha256: staging.ContentSha256);

                AttachProgrammes(revision, parsed.Programmes, source.Id, programmes);
                AttachLegalBasis(revision, parsed.LegalBasis, source.JurisdictionId, legalInstruments);
                revisions.Add(revision);
            }

            // The old revision must close before the new one opens: a unique index enforces
            // at most one open revision per source record, and it is the only thing standing
            // between a bug here and a subject that silently exists twice.
            if (supersededIds.Count > 0)
            {
                await CloseRevisionsAsync(supersededIds, now, ct).ConfigureAwait(false);
                supersededIds.Clear();
            }

            if (revisions.Count > 0)
            {
                db.Designations.AddRange(revisions);
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();
            }
        }

        var missing = current.Values.Where(c => !seen.Contains(c.SourceRecordKey)).ToList();
        var markedMissing = await MarkPossiblyMissingAsync(missing, run.Id, ct).ConfigureAwait(false);

        await TouchAsync(unchangedIds, run.Id, ct).ConfigureAwait(false);
        await RestoreAsync(reappeared, ct).ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        return new ImportOutcome
        {
            ImportRunId = run.Id,
            SourceCode = source.Code,
            Status = ImportStatus.Success,
            Inserted = inserted,
            Updated = updated,
            Unchanged = unchangedIds.Count,
            MarkedPossiblyMissing = markedMissing,
            Reappeared = reappeared.Count,
        };
    }

    private async Task CloseRevisionsAsync(List<Guid> ids, DateTimeOffset now, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(1000))
        {
            await db.Designations
                .Where(d => chunk.Contains(d.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.ValidTo, now), ct)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The publisher stopped sending these. That is not the same as a delisting — a
    /// truncated file, a schema change or a regional outage look identical from here — so
    /// they are marked and left in place for a human to decide on.
    /// </summary>
    private async Task<int> MarkPossiblyMissingAsync(List<CurrentRevision> missing, Guid runId, CancellationToken ct)
    {
        var ids = missing
            .Where(m => m.Status == DesignationStatus.Active)
            .Select(m => m.Id)
            .ToList();

        var marked = 0;

        foreach (var chunk in ids.Chunk(1000))
        {
            marked += await db.Designations
                .Where(d => chunk.Contains(d.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DesignationStatus.PossiblyMissing), ct)
                .ConfigureAwait(false);
        }

        if (marked > 0)
        {
            logger.LogWarning("{Count} designations were absent from this release and are now possibly missing.", marked);
        }

        return marked;
    }

    /// <summary>Records that the current run saw these, which is how "last confirmed" is answered.</summary>
    private async Task TouchAsync(List<Guid> ids, Guid runId, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(1000))
        {
            await db.Designations
                .Where(d => chunk.Contains(d.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.LastSeenRunId, runId), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task RestoreAsync(List<Guid> ids, CancellationToken ct)
    {
        foreach (var chunk in ids.Chunk(1000))
        {
            await db.Designations
                .Where(d => chunk.Contains(d.Id))
                .ExecuteUpdateAsync(s => s.SetProperty(d => d.Status, DesignationStatus.Active), ct)
                .ConfigureAwait(false);
        }
    }

    private async Task<Dictionary<string, Guid>> LoadLegalInstrumentsAsync(Guid jurisdictionId, CancellationToken ct) =>
        await db.LegalInstruments
            .Where(i => i.JurisdictionId == jurisdictionId)
            .ToDictionaryAsync(i => i.InstrumentType + "|" + i.InstrumentNumber, i => i.Id, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

    private async Task<Dictionary<string, Guid>> LoadProgrammesAsync(Guid sourceId, CancellationToken ct) =>
        await db.Programmes
            .Where(p => p.SourceId == sourceId)
            .ToDictionaryAsync(p => p.Code, p => p.Id, StringComparer.Ordinal, ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Acts are shared: 6 234 subjects cite 462 distinct regulations, so they are stored
    /// once and linked, keyed the way the unique index is — jurisdiction, type, number.
    /// </summary>
    private void AttachLegalBasis(
        Designation revision,
        IReadOnlyList<ParsedLegalInstrument> basis,
        Guid jurisdictionId,
        Dictionary<string, Guid> known)
    {
        foreach (var act in basis)
        {
            var key = $"{act.InstrumentType}|{act.InstrumentNumber}";

            if (!known.TryGetValue(key, out var instrumentId))
            {
                instrumentId = Guid.CreateVersion7();
                known[key] = instrumentId;

                db.LegalInstruments.Add(new LegalInstrument
                {
                    Id = instrumentId,
                    JurisdictionId = jurisdictionId,
                    InstrumentType = act.InstrumentType,
                    InstrumentNumber = act.InstrumentNumber,
                    Title = act.Title,
                    PublicationDate = act.PublicationDate,
                    EffectiveDate = act.EffectiveDate,
                    OfficialUrl = act.OfficialUrl,
                    CelexNumber = act.CelexNumber,
                });
            }

            revision.LegalInstruments.Add(new DesignationLegalInstrument
            {
                LegalInstrumentId = instrumentId,
                Relation = act.Relation,
            });
        }
    }

    private void AttachProgrammes(
        Designation revision,
        IReadOnlyList<string> codes,
        Guid sourceId,
        Dictionary<string, Guid> known)
    {
        foreach (var code in codes)
        {
            if (!known.TryGetValue(code, out var programmeId))
            {
                programmeId = Guid.CreateVersion7();
                known[code] = programmeId;

                db.Programmes.Add(new SanctionProgramme
                {
                    Id = programmeId,
                    SourceId = sourceId,
                    Code = code,
                });
            }

            revision.Programmes.Add(new DesignationProgramme { ProgrammeId = programmeId });
        }
    }

    private async Task ClearStagingAsync(Guid runId, CancellationToken ct) =>
        await db.ImportStaging
            .Where(s => s.ImportRunId == runId)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

    private async Task FinishAsync(ImportRun run, string status, string? error, CancellationToken ct)
    {
        run.Status = status;
        run.FinishedAt = clock.GetUtcNow();
        run.ErrorSummary = Truncate(error, 2000);

        // EF's relationship fixup pointed run.Source at the tracked source row when the run
        // was added. Left in place, re-attaching the run walks that navigation and rewrites
        // sanctions_source on every import — a configuration table quietly touched by a
        // data job, for no reason.
        run.Source = null;

        if (db.Entry(run).State == EntityState.Detached)
        {
            db.Attach(run);
        }

        db.Entry(run).State = EntityState.Modified;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    private ImportOutcome Outcome(
        ImportRun run, SanctionsSource source, ImportStatus status, Stopwatch started, string? reason) =>
        new()
        {
            ImportRunId = run.Id,
            SourceCode = source.Code,
            Status = status,
            Reason = reason,
            Duration = started.Elapsed,
        };

    private static string? Truncate(string? value, int max) =>
        value is null || value.Length <= max ? value : value[..max];

    private sealed record CurrentRevision(
        Guid Id, string SourceRecordKey, string ContentSha256, DesignationStatus Status, Guid FirstSeenRunId);
}
