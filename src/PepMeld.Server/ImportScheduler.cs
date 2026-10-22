using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;

namespace PepMeld.Server;

/// <summary>
/// Keeps every enabled source fresh: once an hour it looks for sources whose last
/// successful import is older than their refresh interval and imports them, one at a
/// time. Nothing runs at startup for its own sake, so restarting the service in
/// development does not hit the publishers; a fresh database imports on the first tick.
///
/// Turn it off with PepMeld:Jobs:Enabled=false. The CLI ("import CZ_PSP") remains for a
/// first fill or a forced re-import.
/// </summary>
internal sealed class ImportScheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<ImportScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // A short first delay so the web host is up and a migration has run before the
        // first import; an hour afterwards.
        var delay = TimeSpan.FromSeconds(30);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(delay, clock, stoppingToken);
                await TickAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "The import tick failed; trying again in an hour.");
            }

            delay = Interval;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PepMeldDbContext>();
        var importer = scope.ServiceProvider.GetRequiredService<PepImportService>();
        var now = clock.GetUtcNow();

        var sources = await db.Sources.AsNoTracking().Where(s => s.IsEnabled).ToListAsync(ct);
        var imported = false;

        foreach (var source in sources)
        {
            var last = await db.ImportRuns.AsNoTracking()
                .Where(r => r.SourceId == source.Id && (r.Status == ImportStatus.Success || r.Status == ImportStatus.SkippedUnchanged))
                .MaxAsync(r => (DateTimeOffset?)r.StartedAt, ct);

            if (last is { } at && now - at < TimeSpan.FromHours(source.RefreshHours))
            {
                continue;
            }

            logger.LogInformation("Importing {Source}: last successful run {Last}.", source.Code, last?.ToString("u") ?? "never");
            await importer.RunAsync(source.Code, force: false, ct);
            imported = true;
        }

        // After the lists, so a date published since or a lender gone is seen the same tick.
        if (imported)
        {
            await scope.ServiceProvider.GetRequiredService<BirthDateFill>().RunAsync(ct);

            // A new mayor in the register of authorities gets years of birth from the
            // election; with nobody new it does not download anything.
            await scope.ServiceProvider.GetRequiredService<ElectionAges>().RunAsync(ElectionAges.Kv2026, force: false, ct);
        }

        // Family ties from Wikidata, once a day, after the lists.
        var lastFamily = await db.FamilySyncs.AsNoTracking()
            .Where(s => s.Source == PepMeld.Connectors.Wikidata.WikidataFamilySync.SourceCode && s.Status == ImportStatus.Success)
            .MaxAsync(s => (DateTimeOffset?)s.StartedAt, ct);

        if (lastFamily is null || now - lastFamily >= TimeSpan.FromHours(24))
        {
            await scope.ServiceProvider.GetRequiredService<PepMeld.Connectors.Wikidata.WikidataFamilySync>().SyncAsync(ct);
        }
    }
}
