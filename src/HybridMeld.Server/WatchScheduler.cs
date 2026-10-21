using HybridMeld.Core.Links;
using HybridMeld.Core.Sources;

namespace HybridMeld.Server;

/// <summary>
/// Keeps watched sources fresh and links checked. Every fifteen minutes it fetches the
/// sources whose last successful run is older than their refresh interval (a Telegram
/// channel defaults to hourly, a feed to daily) and, once a day, verifies the links to
/// sanctions and PEP records that have not been looked at for a week. Nothing runs at
/// startup for its own sake.
///
/// Turn it off with HybridMeld:Jobs:Enabled=false. The CLI ("hybrid sources fetch") remains.
/// </summary>
internal sealed class WatchScheduler(IServiceScopeFactory scopes, TimeProvider clock, ILogger<WatchScheduler> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(15);
    private DateTimeOffset? linksCheckedAt;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
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
                logger.LogError(ex, "The watch tick failed; trying again in {Minutes} minutes.", Interval.TotalMinutes);
            }

            delay = Interval;
        }
    }

    private async Task TickAsync(CancellationToken ct)
    {
        using var scope = scopes.CreateScope();
        var fetch = scope.ServiceProvider.GetRequiredService<SourceFetchService>();

        foreach (var code in await fetch.DueAsync(ct))
        {
            // One failing source must not stop the others; the run row carries the error.
            await fetch.RunAsync(code, ct);
        }

        var now = clock.GetUtcNow();
        if (linksCheckedAt is null || now - linksCheckedAt >= TimeSpan.FromHours(24))
        {
            await scope.ServiceProvider.GetRequiredService<LinkVerifier>().VerifyAsync(1000, all: false, ct);
            linksCheckedAt = now;
        }
    }
}
