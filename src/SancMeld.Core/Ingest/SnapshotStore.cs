using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Core.Ingest;

/// <summary>
/// Keeps the bytes that were downloaded, before anything interprets them.
///
/// This is what makes a mapping bug fixable: the file a decision was based on can be
/// replayed months later without asking the publisher for an archive it does not keep.
/// </summary>
public interface ISnapshotStore
{
    /// <summary>Returns the URI recorded on the snapshot row.</summary>
    Task<string> SaveAsync(string sourceCode, string sha256, Stream content, CancellationToken ct);
}

public sealed class FileSystemSnapshotStore(IOptions<SancMeldOptions> options) : ISnapshotStore
{
    public async Task<string> SaveAsync(string sourceCode, string sha256, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var root = Path.GetFullPath(options.Value.Ingest.SnapshotStoragePath);

        // Laid out by source and content hash, so the same bytes downloaded twice occupy
        // one file and the name itself proves which bytes these are.
        var directory = Path.Combine(root, sourceCode);
        Directory.CreateDirectory(directory);

        var path = Path.Combine(directory, $"{sha256}.bin");
        if (File.Exists(path))
        {
            return path;
        }

        // Written under a temporary name and moved into place, so an interrupted run
        // cannot leave a half-file that later looks like a complete snapshot.
        var pending = path + ".partial";

        try
        {
            await using (var target = new FileStream(
                pending, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 64 * 1024, FileOptions.Asynchronous))
            {
                await content.CopyToAsync(target, ct).ConfigureAwait(false);
            }

            File.Move(pending, path, overwrite: true);
        }
        catch
        {
            File.Delete(pending);
            throw;
        }

        return path;
    }
}
