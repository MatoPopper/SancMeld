using System.Globalization;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using System.Xml;
using System.Xml.Linq;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Ingest;

namespace SancMeld.Connectors.EuFsf;

/// <summary>
/// EU Consolidated Financial Sanctions List (FSF), published by the European Commission.
/// Legally binding inside the EU.
///
/// Two properties of the endpoint shape this connector, both verified against the live
/// service on 2026-08-06:
///
/// It ignores conditional requests. If-Modified-Since with the exact Last-Modified it
/// had just sent returns 200 and all 25 MB. So change detection cannot use HTTP; it uses
/// the 3.9 kB RSS feed, which advertises the SHA-1 of each published file.
///
/// That advertised SHA-1 is genuinely the hash of the file. It is therefore also an
/// integrity check the publisher hands us for free, and a truncated transfer is caught
/// before a single record is interpreted.
/// </summary>
public sealed partial class EuFsfConnector(
    HttpClient httpClient,
    EuFsfOptions options,
    IngestOptions ingestOptions) : ISanctionsConnector
{
    public string SourceCode => "EU_FSF";

    private readonly IngestGuards _guards = new() { MinimumRecordCount = options.MinimumRecordCount };

    public async Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        try
        {
            using var response = await SendAsync(HttpMethod.Get, options.RssUrl, ct).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var feed = XDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
            var item = FindItem(feed)
                ?? throw new EuFsfParseException(
                    $"The RSS feed lists no file matching '{options.FileVariant}'. The published formats have changed.");

            var enclosure = item.Element("enclosure");
            var url = enclosure?.Attribute("url")?.Value ?? item.Element("link")?.Value
                ?? throw new EuFsfParseException("The matched RSS item carries no download URL.");

            var checksum = ChecksumPattern().Match(item.Element("guid")?.Value ?? string.Empty);

            headers["discovery"] = "rss";
            Add(headers, "rss.pubDate", item.Element("pubDate")?.Value);
            Add(headers, "rss.length", enclosure?.Attribute("length")?.Value);
            Add(headers, "rss.sha1", checksum.Success ? checksum.Groups[1].Value.ToUpperInvariant() : null);

            return new SourceDiscoveryResult(
                WithConfiguredToken(new Uri(url)),

                // The advertised SHA-1 is the version marker: it changes exactly when the
                // content does, unlike a build date, which moves on every republication.
                checksum.Success ? checksum.Groups[1].Value.ToUpperInvariant() : null,
                headers);
        }
        catch (Exception ex) when (ex is HttpRequestException or XmlException or EuFsfParseException or TaskCanceledException)
        {
            // The feed is an optimisation, not the source. Losing it costs a full download
            // per run and the ability to skip unchanged releases, but it must not stop the
            // sanctions list from being fetched at all. Recorded so the degradation shows
            // up in the audit trail instead of looking like normal operation.
            headers["discovery"] = "fallback";
            headers["discovery.reason"] = ex.Message;

            return new SourceDiscoveryResult(options.BuildDownloadUrl(), ExternalVersion: null, headers);
        }
    }

    public async Task<DownloadResult> DownloadAsync(
        SourceDiscoveryResult discovery,
        ConditionalRequestState conditional,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(conditional);

        if (discovery.ExternalVersion is { Length: > 0 } advertised &&
            string.Equals(advertised, conditional.PreviousExternalVersion, StringComparison.OrdinalIgnoreCase))
        {
            // Status 0 rather than 304: no request was made, and recording a status the
            // server never sent would be a lie in the audit trail.
            return new DownloadResult(
                Stream.Null, HttpStatus: 0, conditional.PreviousSha256 ?? string.Empty,
                ByteSize: 0, ContentType: null, ETag: null, LastModified: null)
            {
                Unchanged = true,
            };
        }

        using var response = await SendAsync(HttpMethod.Get, discovery.DownloadUrl, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var (content, sha256, sha1, byteSize) = await BufferAsync(response, ct).ConfigureAwait(false);

        try
        {
            VerifyPublishedChecksum(discovery, sha1, byteSize);
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
        CancellationToken ct) => EuFsfXmlParser.ParseAsync(source, ct);

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.RecordCount < _guards.MinimumRecordCount)
        {
            return Task.FromResult(DatasetValidationResult.Reject(
                $"Parsed {stats.RecordCount} subjects, fewer than the {_guards.MinimumRecordCount} required. " +
                "The EU list does not shrink to this size; a truncated download or an error page served as 200 is far likelier."));
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

        var total = stats.RecordCount + stats.ParseErrorCount;
        if (total > 0)
        {
            var errorRate = stats.ParseErrorCount / (double)total;
            if (errorRate > _guards.MaxParseErrorFraction)
            {
                return Task.FromResult(DatasetValidationResult.Reject(
                    $"{stats.ParseErrorCount} of {total} records failed to parse ({errorRate:P1}), " +
                    $"beyond the permitted {_guards.MaxParseErrorFraction:P0}."));
            }
        }

        return Task.FromResult(DatasetValidationResult.Ok());
    }

    /// <summary>
    /// Matched on the URL path, not the item title: the titles are prose
    /// ("XML (Based on XSD) - v1.1") and will be reworded eventually, while the path
    /// segment is the identifier the endpoint itself is addressed by.
    /// </summary>
    private XElement? FindItem(XDocument feed)
    {
        var marker = $"/files/{options.FileVariant}/content";

        return feed.Descendants("item").FirstOrDefault(item =>
            (item.Element("enclosure")?.Attribute("url")?.Value ?? item.Element("link")?.Value ?? string.Empty)
            .Contains(marker, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The feed's own links carry the Commission's published token. Replacing it with the
    /// configured one keeps a deployment that was given a different token working, while
    /// still following the feed if the path ever moves.
    /// </summary>
    private Uri WithConfiguredToken(Uri url) =>
        new UriBuilder(url) { Query = "token=" + Uri.EscapeDataString(options.Token) }.Uri;

    private void VerifyPublishedChecksum(SourceDiscoveryResult discovery, string sha1, long byteSize)
    {
        if (!options.VerifyPublishedChecksum ||
            discovery.Headers is not { } headers ||
            !headers.TryGetValue("rss.sha1", out var advertised) ||
            string.IsNullOrEmpty(advertised))
        {
            return;
        }

        if (!string.Equals(advertised, sha1, StringComparison.OrdinalIgnoreCase))
        {
            throw new EuFsfParseException(
                $"The downloaded file does not match the checksum the publisher advertised " +
                $"(expected {advertised}, got {sha1}, {byteSize} bytes). Refusing to interpret it.");
        }

        if (headers.TryGetValue("rss.length", out var length) &&
            long.TryParse(length, NumberStyles.None, CultureInfo.InvariantCulture, out var expected) &&
            expected != byteSize)
        {
            throw new EuFsfParseException(
                $"The downloaded file is {byteSize} bytes, but the publisher advertised {expected}.");
        }
    }

    /// <summary>
    /// Spools the response to a temporary file, hashing as it goes, and hands back a
    /// stream over it. The file is 25 MB and the parser needs to read it from the start,
    /// so it cannot stay a network stream — and holding it in memory would put it
    /// straight onto the large object heap on every scheduled run.
    ///
    /// The returned stream deletes its file on dispose; a caller that wants to keep the
    /// artefact (the StoreSnapshot phase) copies it first.
    /// </summary>
    private static async Task<(Stream Content, string Sha256, string Sha1, long ByteSize)> BufferAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"sancmeld-eufsf-{Guid.NewGuid():N}.tmp"),
            FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None,
            bufferSize: 64 * 1024,
            FileOptions.DeleteOnClose | FileOptions.Asynchronous);

        try
        {
            using var sha256 = SHA256.Create();

            // Not a security decision: SHA-1 is the algorithm the publisher chose for the
            // checksum in its feed, so it is what a comparison has to use.
            using var sha1 = SHA1.Create();

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
                sha1.TransformBlock(buffer, 0, read, null, 0);
                await file.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                total += read;
            }

            sha256.TransformFinalBlock([], 0, 0);
            sha1.TransformFinalBlock([], 0, 0);

            await file.FlushAsync(ct).ConfigureAwait(false);
            file.Position = 0;

            return (file, Convert.ToHexString(sha256.Hash!).ToLowerInvariant(), Convert.ToHexString(sha1.Hash!), total);
        }
        catch
        {
            await file.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, Uri url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);

        // Required, and not only by OFAC: an anonymous scraper is what publishers block
        // first, and the header is how they reach whoever is calling them.
        request.Headers.TryAddWithoutValidation("User-Agent", ingestOptions.UserAgent);

        return await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    private static void Add(Dictionary<string, string> headers, string key, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            headers[key] = value;
        }
    }

    [GeneratedRegex(@"checksum=([0-9A-Fa-f]{40})", RegexOptions.IgnoreCase)]
    private static partial Regex ChecksumPattern();
}
