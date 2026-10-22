using Microsoft.Extensions.Logging;

namespace HybridMeld.Core.Sources;

/// <summary>
/// A best-effort copy of a cited page, shared by claims and hypotheses. A fetch that
/// fails returns null and the caller saves the quote without a snapshot: a paywalled or
/// deleted page is exactly when the quote matters most, so it is never a reason to
/// refuse the record.
/// </summary>
public sealed class PageCapture(PageFetcher pages, ISnapshotStore snapshots, ILogger<PageCapture> logger)
{
    public sealed record Captured(string Path, string Sha256, string? Title);

    public async Task<Captured?> TryAsync(Uri uri, string folder, CancellationToken ct)
    {
        try
        {
            var page = await pages.FetchAsync(uri, ct).ConfigureAwait(false);
            if (page is null)
            {
                return null;
            }

            var (path, sha) = await snapshots.SaveAsync(folder, page.Bytes, ct).ConfigureAwait(false);
            return new Captured(path, sha, page.Title);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            logger.LogWarning(ex, "Could not keep a copy of {Url}; the quote stands on its own.", uri);
            return null;
        }
    }
}
