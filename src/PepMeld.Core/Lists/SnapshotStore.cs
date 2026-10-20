using System.Security.Cryptography;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;

namespace PepMeld.Core.Lists;

/// <summary>Keeps the downloaded bytes, by source and content hash, before anything interprets them.</summary>
public interface ISnapshotStore
{
    /// <summary>Writes the stream to disk and returns the path and the SHA-256 of what was written.</summary>
    Task<(string Path, string Sha256)> SaveAsync(string sourceCode, Stream content, CancellationToken ct);
}

public sealed class FileSystemSnapshotStore(IOptions<PepMeldOptions> options) : ISnapshotStore
{
    public async Task<(string Path, string Sha256)> SaveAsync(string sourceCode, Stream content, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(content);

        var directory = Path.Combine(Path.GetFullPath(options.Value.Ingest.SnapshotStoragePath), sourceCode);
        Directory.CreateDirectory(directory);

        // The hash is only known once the bytes are through, so they land under a
        // temporary name and are moved to their final one; an interrupted run cannot leave
        // a half-file that later looks like a complete snapshot.
        var pending = Path.Combine(directory, Guid.CreateVersion7().ToString("N") + ".partial");

        try
        {
            using var sha = SHA256.Create();
            await using (var target = new FileStream(pending, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, FileOptions.Asynchronous))
            await using (var hashing = new CryptoStream(target, sha, CryptoStreamMode.Write))
            {
                await content.CopyToAsync(hashing, ct).ConfigureAwait(false);
            }

            var hex = Convert.ToHexStringLower(sha.Hash!);
            var path = Path.Combine(directory, hex + ".bin");

            if (File.Exists(path))
            {
                File.Delete(pending);
            }
            else
            {
                File.Move(pending, path);
            }

            return (path, hex);
        }
        catch
        {
            File.Delete(pending);
            throw;
        }
    }
}
