using System.Security.Cryptography;
using HybridMeld.Core.Configuration;
using Microsoft.Extensions.Options;

namespace HybridMeld.Core.Sources;

/// <summary>Keeps fetched bytes by folder and content hash, before anything interprets them. The shape PepMeld's store has.</summary>
public interface ISnapshotStore
{
    Task<(string Path, string Sha256)> SaveAsync(string folder, ReadOnlyMemory<byte> content, CancellationToken ct);
}

public sealed class FileSystemSnapshotStore(IOptions<HybridMeldOptions> options) : ISnapshotStore
{
    public async Task<(string Path, string Sha256)> SaveAsync(string folder, ReadOnlyMemory<byte> content, CancellationToken ct)
    {
        var directory = Path.Combine(Path.GetFullPath(options.Value.Ingest.SnapshotStoragePath), Safe(folder));
        Directory.CreateDirectory(directory);

        var hex = Convert.ToHexStringLower(SHA256.HashData(content.Span));
        var path = Path.Combine(directory, hex + ".bin");

        if (!File.Exists(path))
        {
            // Under a temporary name first, so an interrupted write never leaves a file
            // whose name promises a hash its bytes do not have.
            var pending = path + "." + Guid.CreateVersion7().ToString("N") + ".partial";
            try
            {
                await File.WriteAllBytesAsync(pending, content.ToArray(), ct).ConfigureAwait(false);
                File.Move(pending, path, overwrite: false);
            }
            catch (IOException) when (File.Exists(path))
            {
                File.Delete(pending);
            }
            catch
            {
                File.Delete(pending);
                throw;
            }
        }

        return (path, hex);
    }

    private static string Safe(string folder)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string(folder.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
    }
}
