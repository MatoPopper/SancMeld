using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Ingest;

namespace SancMeld.Connectors.UnSc;

/// <summary>
/// UN Security Council Consolidated List. Legally binding inside the EU, which implements
/// UN designations through its own regulations.
///
/// Simpler than the EU connector, and for one reason: this endpoint honours conditional
/// requests. It answers 302 with a signed Azure Blob URL, and the blob returns 304 to
/// If-None-Match (verified 2026-08-08), so an unchanged release costs one round trip and
/// no payload — no catalogue file, no version marker of our own.
///
/// Two consequences of that redirect are worth knowing. The resolved URL carries a
/// one-hour signature, so it must never be stored or reused. And the blob sends a
/// Content-MD5, which is an integrity check the publisher hands us for free.
/// </summary>
public sealed class UnScConnector(
    HttpClient httpClient,
    UnScOptions options,
    IngestOptions ingestOptions) : ISanctionsConnector
{
    public string SourceCode => "UN_SC";

    private readonly IngestGuards _guards = new() { MinimumRecordCount = options.MinimumRecordCount };

    public Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct) =>
        // Nothing to discover: the address is stable and the publisher offers no catalogue
        // to read a version from. Change detection is HTTP's own, in DownloadAsync.
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

        // The ETag belongs to the blob behind the redirect, not to the address we asked
        // for, but it survives the hop and the blob answers 304.
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

        var advertisedMd5 = response.Content.Headers.ContentMD5;
        var (content, sha256, md5, byteSize) = await BufferAsync(response, ct).ConfigureAwait(false);

        try
        {
            VerifyChecksum(advertisedMd5, md5, byteSize);
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
        CancellationToken ct) => UnScXmlParser.ParseAsync(source, ct);

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.RecordCount < _guards.MinimumRecordCount)
        {
            return Task.FromResult(DatasetValidationResult.Reject(
                $"Parsed {stats.RecordCount} subjects, fewer than the {_guards.MinimumRecordCount} required. " +
                "The UN list changes slowly and does not shrink to this size; a truncated download is far likelier."));
        }

        if (stats.PreviousRecordCount is > 0 and var previous)
        {
            var shrink = (previous - stats.RecordCount) / (double)previous;
            if (shrink > _guards.MaxShrinkFraction)
            {
                return Task.FromResult(DatasetValidationResult.Reject(
                    $"Subject count fell from {previous} to {stats.RecordCount} ({shrink:P1}), " +
                    $"beyond the {_guards.MaxShrinkFraction:P0} a single release may lose."));
            }
        }

        return Task.FromResult(DatasetValidationResult.Ok());
    }

    private void VerifyChecksum(byte[]? advertised, string actual, long byteSize)
    {
        if (!options.VerifyContentMd5 || advertised is null or { Length: 0 })
        {
            return;
        }

        var expected = Convert.ToHexString(advertised);
        if (!string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
        {
            throw new UnScParseException(
                $"The downloaded file does not match the Content-MD5 the publisher advertised " +
                $"(expected {expected}, got {actual}, {byteSize} bytes). Refusing to interpret it.");
        }
    }

    /// <summary>
    /// Spools the response to a temporary file, hashing as it goes. The parser reads from
    /// the start, so it cannot stay a network stream; the file deletes itself on dispose.
    /// </summary>
    private static async Task<(Stream Content, string Sha256, string Md5, long ByteSize)> BufferAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"sancmeld-unsc-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            using var sha256 = SHA256.Create();

            // Not a security decision: MD5 is what Azure puts in Content-MD5, so it is what
            // a comparison against the publisher's own checksum has to use.
            using var md5 = MD5.Create();

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
                md5.TransformBlock(buffer, 0, read, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            sha256.TransformFinalBlock([], 0, 0);
            md5.TransformFinalBlock([], 0, 0);

            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Position = 0;

            return (file, Convert.ToHexString(sha256.Hash!).ToLowerInvariant(), Convert.ToHexString(md5.Hash!), total);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
