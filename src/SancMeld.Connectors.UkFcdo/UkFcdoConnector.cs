using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Ingest;

namespace SancMeld.Connectors.UkFcdo;

/// <summary>
/// The UK Sanctions List — every designation under the Sanctions and Anti-Money
/// Laundering Act 2018, published by the FCDO. Since 28 January 2026 it is the UK's
/// only list: the OFSI Consolidated List that preceded it has closed.
///
/// Advisory only, like OFAC: a UK designation obliges nobody in the EU. It is imported
/// as a red flag, and for what it records that the EU does not — 5 360 of its 6 408
/// designations on 2026-10-15 are the UK's own, not UN transpositions, and the UK lists
/// under regimes the EU has no counterpart of (global anti-corruption, for one).
///
/// The XML has a stable address and the server honours ETags, so there is nothing to
/// discover and an unchanged list costs one 304 instead of 22 MB.
/// </summary>
public sealed class UkFcdoConnector(
    HttpClient httpClient,
    UkFcdoOptions options,
    IngestOptions ingestOptions) : ISanctionsConnector
{
    public string SourceCode => "UK_FCDO";

    private readonly IngestGuards _guards = new() { MinimumRecordCount = options.MinimumRecordCount };

    public Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct) =>
        // DateGenerated is in the payload, after the download it would have let us skip.
        // Change detection is the server's ETag.
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
            await RequireDesignationsAsync(content, byteSize, ct).ConfigureAwait(false);
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
        CancellationToken ct) => UkFcdoXmlParser.ParseAsync(source, ct);

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.RecordCount < _guards.MinimumRecordCount)
        {
            return Task.FromResult(DatasetValidationResult.Reject(
                $"Parsed {stats.RecordCount} designations, fewer than the {_guards.MinimumRecordCount} required. " +
                "The UK list held 6 408 on 2026-10-15 and does not shrink to this size; a truncated download " +
                "or a page served as the file is far likelier."));
        }

        if (stats.PreviousRecordCount is > 0 and var previous)
        {
            var shrink = (previous - stats.RecordCount) / (double)previous;
            if (shrink > _guards.MaxShrinkFraction)
            {
                return Task.FromResult(DatasetValidationResult.Reject(
                    $"Designation count fell from {previous} to {stats.RecordCount} ({shrink:P1}), " +
                    $"beyond the {_guards.MaxShrinkFraction:P0} a single release may lose."));
            }
        }

        return Task.FromResult(DatasetValidationResult.Ok());
    }

    /// <summary>
    /// The document starts with its XML declaration and a &lt;Designations&gt; root. A
    /// CloudFront error page served with 200 does not, and refusing it here names the
    /// cause instead of an XML exception from inside the reader.
    /// </summary>
    private static async Task RequireDesignationsAsync(Stream content, long byteSize, CancellationToken ct)
    {
        var head = new byte[256];
        var read = await content.ReadAtLeastAsync(head, 1, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        content.Position = 0;

        var text = Encoding.UTF8.GetString(head, 0, read).TrimStart('﻿', ' ', '\r', '\n');

        if (!text.Contains("<Designations", StringComparison.Ordinal))
        {
            throw new UkFcdoParseException(
                $"The download ({byteSize} bytes) does not open with <Designations>; it begins '{text[..Math.Min(text.Length, 40)]}'. " +
                "The server answered with something other than the list.");
        }
    }

    /// <summary>
    /// Spools to a temporary file, hashing as it goes. The file is 22 MB and the parser
    /// reads it from the start, so it cannot stay a network stream, and holding it in
    /// memory would put it on the large object heap on every scheduled run. The file
    /// deletes itself on dispose.
    /// </summary>
    private static async Task<(Stream Content, string Sha256, long ByteSize)> BufferAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"sancmeld-ukfcdo-{Guid.NewGuid():N}.tmp"),
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
