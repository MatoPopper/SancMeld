using Microsoft.Extensions.Options;
using PepMeld.Core.Checks;
using PepMeld.Core.Configuration;

namespace PepMeld.Server;

/// <summary>
/// Takes check ids off the queue and runs them, a few at a time. On start it fails every
/// check the previous process left running: that process is gone, and a user waiting on
/// its check would otherwise wait forever and be refused a new one.
/// </summary>
internal sealed class CheckWorker(
    CheckQueue queue,
    IServiceScopeFactory scopes,
    IOptions<PepMeldOptions> options,
    ILogger<CheckWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await InterruptLeftoversAsync(stoppingToken);

        using var slots = new SemaphoreSlim(options.Value.Checks.MaxParallel);
        var running = new List<Task>();

        await foreach (var checkId in queue.Reader.ReadAllAsync(stoppingToken))
        {
            await slots.WaitAsync(stoppingToken);
            running.RemoveAll(t => t.IsCompleted);
            running.Add(RunOneAsync(checkId, slots, stoppingToken));
        }

        await Task.WhenAll(running);
    }

    private async Task RunOneAsync(Guid checkId, SemaphoreSlim slots, CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var runner = scope.ServiceProvider.GetRequiredService<CheckRunner>();
            await runner.RunAsync(checkId, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            // Shutting down; the check is marked interrupted on the next start.
        }
        catch (Exception ex)
        {
            // The runner records stage failures itself; this is for what happens before
            // it gets the chance, such as the database being away.
            logger.LogError(ex, "Check {CheckId} could not be run.", checkId);
        }
        finally
        {
            slots.Release();
        }
    }

    private async Task InterruptLeftoversAsync(CancellationToken ct)
    {
        try
        {
            using var scope = scopes.CreateScope();
            var checks = scope.ServiceProvider.GetRequiredService<CheckService>();
            var interrupted = await checks.InterruptRunningAsync("Prerušené reštartom služby; zadajte preverenie znova.", ct);
            if (interrupted > 0)
            {
                logger.LogWarning("Marked {Count} check(s) left running by a previous process as failed.", interrupted);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex, "Could not mark leftover checks as interrupted; they will stay 'running' until the next start.");
        }
    }
}
