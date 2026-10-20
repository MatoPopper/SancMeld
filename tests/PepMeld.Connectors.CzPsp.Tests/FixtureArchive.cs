using System.IO.Compression;

namespace PepMeld.Connectors.CzPsp.Tests;

/// <summary>The fixture directory zipped in memory, the shape the publisher serves.</summary>
public static class FixtureArchive
{
    public const string Directory = "fixtures/cz-psp";

    public static MemoryStream Zip(string directory = Directory)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in System.IO.Directory.GetFiles(directory, "*.unl"))
            {
                var entry = archive.CreateEntry(Path.GetFileName(file));
                using var target = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(target);
            }
        }

        stream.Position = 0;
        return stream;
    }
}
