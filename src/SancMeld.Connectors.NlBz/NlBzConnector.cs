using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Ingest;

namespace SancMeld.Connectors.NlBz;

/// <summary>
/// Nationale sanctielijst terrorisme — the Dutch national list of persons and
/// organisations whose assets are frozen under the Sanctieregeling terrorisme 2007-II,
/// the Netherlands' implementation of UN Security Council Resolution 1373.
///
/// Small and homogeneous: 117 entries on 2026-10-15, almost all of them persons born
/// in the Netherlands or North Africa, listed by ministerial decision and published in
/// the Staatscourant. One measure for everyone — the freeze — and no status column:
/// an entry withdrawn is simply gone from the next spreadsheet, which the pipeline
/// records as PossiblyMissing.
///
/// Advisory, like every national list here: the measures bind in the Netherlands.
///
/// The spreadsheet has a stable address and the server honours conditional requests,
/// so there is nothing to discover and an unchanged list costs one 304.
/// </summary>
public sealed class NlBzConnector(
    HttpClient httpClient,
    NlBzOptions options,
    IngestOptions ingestOptions) : ISanctionsConnector
{
    public string SourceCode => "NL_BZ";

    private readonly IngestGuards _guards = new() { MinimumRecordCount = options.MinimumRecordCount };

    public Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct) =>
        // The file carries its own "stand van zaken" date in its title cell, but inside
        // the payload — after the download it would have let us skip. Change detection
        // is the server's ETag.
        Task.FromResult(new SourceDiscoveryResult(options.DownloadUrl, ExternalVersion: null));

    public async Task<DownloadResult> DownloadAsync(
        SourceDiscoveryResult discovery,
        ConditionalRequestState conditional,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(conditional);

        using var request = new HttpRequestMessage(HttpMethod.Get, discovery.DownloadUrl);
        request.Headers.TryAddWithoutValidation("User-Agent", ingestOptions.UserAgent);

        if (conditional.ETag is { Length: > 0 } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        else if (conditional.LastModified is { Length: > 0 } lastModified)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        }

        using var response = await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);

        if (response.StatusCode == HttpStatusCode.NotModified)
        {
            return new DownloadResult(
                Stream.Null, (int)HttpStatusCode.NotModified, conditional.PreviousSha256 ?? string.Empty,
                ByteSize: 0, ContentType: null, ETag: conditional.ETag, LastModified: conditional.LastModified)
            {
                Unchanged = true,
            };
        }

        response.EnsureSuccessStatusCode();

        var (content, sha256, byteSize) = await BufferAsync(response, ct).ConfigureAwait(false);

        try
        {
            await RequireSpreadsheetAsync(content, byteSize, ct).ConfigureAwait(false);
        }
        catch
        {
            await content.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        return new DownloadResult(
            content,
            (int)response.StatusCode,
            sha256,
            byteSize,
            response.Content.Headers.ContentType?.MediaType,
            response.Headers.ETag?.Tag,
            response.Content.Headers.LastModified?.ToString("R", CultureInfo.InvariantCulture));
    }

    public IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        SourceSnapshotMetadata metadata,
        CancellationToken ct) => NlBzOdsParser.ParseAsync(source, ct);

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.RecordCount < _guards.MinimumRecordCount)
        {
            return Task.FromResult(DatasetValidationResult.Reject(
                $"Parsed {stats.RecordCount} entries, fewer than the {_guards.MinimumRecordCount} required. " +
                "The list held 117 on 2026-10-15 and moves by ones and twos; a document this short is " +
                "something other than the list."));
        }

        if (stats.PreviousRecordCount is > 0 and var previous)
        {
            var shrink = (previous - stats.RecordCount) / (double)previous;
            if (shrink > _guards.MaxShrinkFraction)
            {
                return Task.FromResult(DatasetValidationResult.Reject(
                    $"Entry count fell from {previous} to {stats.RecordCount} ({shrink:P1}), " +
                    $"beyond the {_guards.MaxShrinkFraction:P0} a single release may lose."));
            }
        }

        return Task.FromResult(DatasetValidationResult.Ok());
    }

    /// <summary>
    /// An ODS is a zip and starts with "PK". The site answers a moved document with its
    /// own HTML page and status 200, and refusing it here names the cause.
    /// </summary>
    private static async Task RequireSpreadsheetAsync(Stream content, long byteSize, CancellationToken ct)
    {
        var magic = new byte[2];
        var read = await content.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        content.Position = 0;

        if (read < 2 || magic[0] != (byte)'P' || magic[1] != (byte)'K')
        {
            throw new NlBzParseException(
                $"The download ({byteSize} bytes) is not a spreadsheet: it does not start with a zip header. " +
                "The server answered with a page instead of the file.");
        }
    }

    /// <summary>
    /// Spools to a temporary file, hashing as it goes: the zip reader needs a seekable
    /// stream. The file deletes itself on dispose.
    /// </summary>
    private static async Task<(Stream Content, string Sha256, long ByteSize)> BufferAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"sancmeld-nlbz-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            using var sha256 = SHA256.Create();
            await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

            var buffer = new byte[64 * 1024];
            long total = 0;

            while (true)
            {
                var read = await source.ReadAsync(buffer, ct).ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                sha256.TransformBlock(buffer, 0, read, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            sha256.TransformFinalBlock([], 0, 0);
            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Position = 0;

            return (file, Convert.ToHexString(sha256.Hash!).ToLowerInvariant(), total);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
