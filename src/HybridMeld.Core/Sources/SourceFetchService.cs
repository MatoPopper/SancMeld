using System.Diagnostics;
using HybridMeld.Core.Configuration;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HybridMeld.Core.Sources;

/// <summary>
/// One fetch of one source into the inbox: a run row, the fetcher for the source's kind,
/// new items inserted, known ones left alone. Every item's raw form is kept as a
/// snapshot, because what a channel said is the evidence and the channel can delete it.
/// </summary>
public sealed class SourceFetchService(
    HybridMeldDbContext db,
    IEnumerable<ISourceFetcher> fetchers,
    ISnapshotStore snapshots,
    IOptions<HybridMeldOptions> options,
    TimeProvider clock,
    ILogger<SourceFetchService> logger)
{
    public async Task<FetchOutcome> RunAsync(string code, CancellationToken ct)
    {
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Code == code, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The source");

        var fetcher = fetchers.FirstOrDefault(f => f.Kind == source.FetchKind);
        var watch = Stopwatch.StartNew();
        var run = new SourceRun { Id = Guid.CreateVersion7(), SourceId = source.Id, StartedAt = clock.GetUtcNow(), Status = RunStatus.Running };
        db.SourceRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        try
        {
            if (fetcher is null)
            {
                throw new InvalidOperationException($"A {source.FetchKind} source is read by hand; nothing fetches it.");
            }

            var items = await fetcher.FetchAsync(source, ct).ConfigureAwait(false);
            var cap = options.Value.Ingest.MaxItemsPerFetch;
            var maxText = options.Value.Ingest.MaxTextLength;
            var wanted = items.Take(cap).ToList();
            var ids = wanted.Select(i => i.ExternalId).ToList();
            var known = await db.SourceItems.Where(i => i.SourceId == source.Id && ids.Contains(i.ExternalId)).Select(i => i.ExternalId).ToListAsync(ct).ConfigureAwait(false);
            var knownSet = known.ToHashSet(StringComparer.Ordinal);
            var now = clock.GetUtcNow();
            var inserted = 0;

            foreach (var item in wanted.Where(i => !knownSet.Contains(i.ExternalId)))
            {
                var (path, sha) = await snapshots.SaveAsync(source.Code, item.Raw, ct).ConfigureAwait(false);
                db.SourceItems.Add(new SourceItem
                {
                    Id = Guid.CreateVersion7(),
                    SourceId = source.Id,
                    ExternalId = item.ExternalId,
                    Url = item.Url,
                    Title = item.Title is { Length: > 500 } t ? t[..500] : item.Title,
                    Text = item.Text.Length > maxText ? item.Text[..maxText] : item.Text,
                    PublishedAt = item.PublishedAt,
                    FetchedAt = now,
                    Sha256 = sha,
                    SnapshotPath = path,
                });
                knownSet.Add(item.ExternalId);
                inserted++;
            }

            run.ItemsSeen = items.Count;
            run.ItemsNew = inserted;
            run.Status = RunStatus.Success;
            run.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            logger.LogInformation("{Source}: {Seen} items, {New} new.", source.Code, items.Count, inserted);
            return new FetchOutcome(source.Code, RunStatus.Success, items.Count, inserted, watch.Elapsed, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "{Source}: fetch failed.", source.Code);
            db.ChangeTracker.Clear();
            db.SourceRuns.Attach(run);
            run.Status = RunStatus.Failed;
            run.Error = ex.Message.Length > 2000 ? ex.Message[..2000] : ex.Message;
            run.FinishedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return new FetchOutcome(source.Code, RunStatus.Failed, 0, 0, watch.Elapsed, run.Error);
        }
    }

    /// <summary>The sources whose last successful run is older than their refresh interval, or that never ran.</summary>
    public async Task<IReadOnlyList<string>> DueAsync(CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var sources = await db.Sources.AsNoTracking().Where(s => s.IsEnabled && s.FetchKind != FetchKind.Manual).ToListAsync(ct).ConfigureAwait(false);
        var due = new List<string>();

        foreach (var source in sources)
        {
            var last = await db.SourceRuns.AsNoTracking()
                .Where(r => r.SourceId == source.Id && r.Status == RunStatus.Success)
                .MaxAsync(r => (DateTimeOffset?)r.StartedAt, ct)
                .ConfigureAwait(false);

            if (last is null || now - last >= TimeSpan.FromHours(source.RefreshHours))
            {
                due.Add(source.Code);
            }
        }

        return due;
    }
}
