using System.Net;
using Microsoft.Extensions.Logging;

namespace PepMeld.Connectors.SkNrsrVnf;

/// <summary>
/// What every lane goes through before a request. When the site answers 429 (or 503),
/// one lane reports it here and all of them wait: for what Retry-After says, or for a
/// pause that starts at twenty seconds and doubles on each further refusal, up to five
/// minutes. A success resets it. The site has one limit for the client, not one per
/// lane, so the lanes must share one memory of being told to slow down.
/// </summary>
internal sealed class RateLimitGate(TimeProvider clock, ILogger logger)
{
    private static readonly TimeSpan FirstPause = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan LongestPause = TimeSpan.FromMinutes(5);

    private readonly Lock gate = new();
    private DateTimeOffset pausedUntil = DateTimeOffset.MinValue;
    private TimeSpan nextPause = FirstPause;

    public static bool IsRefusal(HttpStatusCode status) =>
        status is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable;

    public async Task WaitAsync(CancellationToken ct)
    {
        while (true)
        {
            TimeSpan remaining;
            lock (gate)
            {
                remaining = pausedUntil - clock.GetUtcNow();
            }

            if (remaining <= TimeSpan.Zero)
            {
                return;
            }

            await Task.Delay(remaining, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>Records a refusal and returns how long the lanes now pause.</summary>
    public TimeSpan Refused(HttpResponseMessage response, string url)
    {
        ArgumentNullException.ThrowIfNull(response);

        var now = clock.GetUtcNow();
        var retryAfter = response.Headers.RetryAfter switch
        {
            { Delta: { } delta } => delta,
            { Date: { } date } => date - now,
            _ => (TimeSpan?)null,
        };

        TimeSpan pause;
        lock (gate)
        {
            pause = retryAfter is { } asked && asked >= TimeSpan.Zero ? asked : nextPause;
            if (retryAfter is null)
            {
                nextPause = nextPause * 2 > LongestPause ? LongestPause : nextPause * 2;
            }

            var until = now + pause;
            if (until > pausedUntil)
            {
                pausedUntil = until;
            }
        }

        logger.LogWarning("{Status} from {Url}; every lane pauses for {Seconds:N0} s.", (int)response.StatusCode, url, pause.TotalSeconds);
        return pause;
    }

    public void Succeeded()
    {
        lock (gate)
        {
            nextPause = FirstPause;
        }
    }
}
