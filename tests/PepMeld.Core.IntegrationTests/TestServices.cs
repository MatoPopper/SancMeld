using System.IO.Compression;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PepMeld.Connectors.CzPsp;
using PepMeld.Core.Checks;
using PepMeld.Core.Configuration;
using PepMeld.Core.Data;
using PepMeld.Core.Lists;

namespace PepMeld.Core.IntegrationTests;

/// <summary>The fixture directory served as the publisher would: a zip, downloaded. Parsed by the real mapper.</summary>
internal sealed class FixtureConnector(string directory = "fixtures/cz-psp") : IPepListConnector
{
    public string SourceCode => CzPspConnector.Code;

    /// <summary>Appended to one file so a second download differs from the first.</summary>
    public string Salt { get; set; } = string.Empty;

    public Task<ListDownload> DownloadAsync(CancellationToken ct)
    {
        var stream = new MemoryStream();

        using (var archive = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var file in Directory.GetFiles(directory, "*.unl"))
            {
                var entry = archive.CreateEntry(Path.GetFileName(file));

                // A zip carries each entry's timestamp; pinned, so the same files give the
                // same bytes and the second download really is unchanged.
                entry.LastWriteTime = new DateTimeOffset(2026, 10, 20, 0, 0, 0, TimeSpan.Zero);
                using var target = entry.Open();
                using var source = File.OpenRead(file);
                source.CopyTo(target);

                if (Salt.Length > 0 && Path.GetFileName(file) == "typ_organu.unl")
                {
                    var bytes = System.Text.Encoding.ASCII.GetBytes("\n999|||" + Salt + "|||\n");
                    target.Write(bytes);
                }
            }
        }

        stream.Position = 0;
        return Task.FromResult(new ListDownload(stream, "fixture://cz-psp", null));
    }

    public IReadOnlyList<ParsedPerson> Parse(Stream content)
    {
        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);

        // The day the fixture was cut: a senator elected in 2022 stays in office under the tests.
        return PspMapper.Map(archive, new DateOnly(2026, 10, 22));
    }
}

/// <summary>The service graph by hand, the way the host wires it.</summary>
internal static class TestServices
{
    public static IOptions<PepMeldOptions> Options(string snapshotRoot) =>
        Microsoft.Extensions.Options.Options.Create(new PepMeldOptions
        {
            Ingest = new IngestOptions { SnapshotStoragePath = snapshotRoot },
        });

    public static PepImportService Importer(PepMeldDbContext db, IPepListConnector connector, IOptions<PepMeldOptions> options) =>
        new(db, [connector], new FileSystemSnapshotStore(options), TimeProvider.System, NullLogger<PepImportService>.Instance);

    public static CheckService Checks(PepMeldDbContext db, CheckQueue? queue = null) =>
        new(db, queue ?? new CheckQueue(), TimeProvider.System);

    public static CheckRunner Runner(PepMeldDbContext db, IOptions<PepMeldOptions> options) =>
        new(db,
            [new ListsStage(new PepListMatcher(db, options), new SourceQueryService(db), options, TimeProvider.System)],
            options,
            TimeProvider.System,
            NullLogger<CheckRunner>.Instance);

    public static string TempDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), "pepmeld-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        return path;
    }
}
