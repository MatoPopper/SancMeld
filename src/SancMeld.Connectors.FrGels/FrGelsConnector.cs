using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Ingest;

namespace SancMeld.Connectors.FrGels;

/// <summary>
/// Registre national des gels — the French asset-freeze register, published by the
/// Direction générale du Trésor.
///
/// Imported for what it records about subjects the EU and the UN already list. 2 051 of
/// its records name the EU designation they correspond to and 337 name the UN one, and on
/// those the date of birth and the nationality were written down twice, by two
/// independent authorities. That is the one thing an alias pair can never be: the same
/// subject with qualifiers that may legitimately differ.
///
/// A national register is deliberately Advisory here, and it is the only source so far
/// where that word understates the truth. These measures bind in France. They do not bind
/// a Slovak or Irish institution, and this product's promise is that "binding" means
/// binding on the reader — so a French listing is shown, always, as something to look at
/// rather than something that obliges. The distinction the model is missing is
/// "binding in one member state", not this listing's importance.
/// </summary>
public sealed class FrGelsConnector(
    HttpClient httpClient,
    FrGelsOptions options,
    IngestOptions ingestOptions) : ISanctionsConnector
{
    public string SourceCode => "FR_GELS";

    private readonly IngestGuards _guards = new() { MinimumRecordCount = options.MinimumRecordCount };

    public Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct) =>
        // The API always serves the current publication and offers no catalogue to read a
        // version from beforehand. The publication carries its own date, but inside the
        // payload — which is to say, after the download it would have let us skip.
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
        CancellationToken ct) => FrGelsJsonParser.ParseAsync(source, ct);

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.RecordCount < _guards.MinimumRecordCount)
        {
            return Task.FromResult(DatasetValidationResult.Reject(
                $"Parsed {stats.RecordCount} records, fewer than the {_guards.MinimumRecordCount} required. " +
                "The register held 6 578 on 2026-08-19 and grows; a truncated download or an error " +
                "page served as 200 is far likelier than a list this size."));
        }

        if (stats.PreviousRecordCount is > 0 and var previous)
        {
            var shrink = (previous - stats.RecordCount) / (double)previous;
            if (shrink > _guards.MaxShrinkFraction)
            {
                return Task.FromResult(DatasetValidationResult.Reject(
                    $"Record count fell from {previous} to {stats.RecordCount} ({shrink:P1}), " +
                    $"beyond the {_guards.MaxShrinkFraction:P0} a single publication may lose."));
            }
        }

        return Task.FromResult(DatasetValidationResult.Ok());
    }

    /// <summary>
    /// Spools to a temporary file, hashing as it goes: the parser reads from the start, so
    /// the payload cannot stay a network stream. The file deletes itself on dispose.
    /// </summary>
    private static async Task<(Stream Content, string Sha256, long ByteSize)> BufferAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"sancmeld-frgels-{Guid.NewGuid():N}.tmp"),
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
