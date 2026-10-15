using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using AngleSharp.Html.Parser;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Ingest;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// Lista osób i podmiotów objętych sankcjami — the Polish national sanctions list, kept
/// by the Minister of the Interior and Administration under the act of 13 April 2022
/// (Dz.U. 2022 poz. 835).
///
/// Poland lists subjects the EU does not: Polish subsidiaries of Russian groups, Belarusian
/// officials, and people named on UK or Ukrainian lists only. Of its 584 rows on
/// 2026-10-15, most cite EU Regulation 269/2014 or 765/2006 as the definition of the
/// assets frozen, not as the reason for listing — the reasons are the ministry's own and
/// are published in Polish prose alongside each decision.
///
/// Advisory, like every national list here: these measures bind in Poland. The list also
/// carries measures no other source has — exclusion from public procurement and an entry
/// ban via the register of undesirable foreigners — and some rows carry only those, so
/// the measures are read per row rather than assumed per source.
///
/// The publication is a gov.pl attachment, addressed by a GUID that changes with every
/// release. Discovery reads the landing page to find it; the GUID doubles as the version
/// marker, so an unchanged release costs one page fetch and no download.
/// </summary>
public sealed class PlMswiaConnector(
    HttpClient httpClient,
    PlMswiaOptions options,
    IngestOptions ingestOptions) : ISanctionsConnector
{
    public string SourceCode => "PL_MSWIA";

    private readonly IngestGuards _guards = new() { MinimumRecordCount = options.MinimumRecordCount };

    public async Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct)
    {
        using var response = await SendAsync(options.LandingPageUrl, conditional: null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var html = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var document = await new HtmlParser().ParseDocumentAsync(html, ct).ConfigureAwait(false);

        // gov.pl renders every attachment as <a class="file-download"> with the file name
        // in a child span. The page also links the ministerial decisions, which are PDFs,
        // and the list's own history, so the workbook is identified by its extension and
        // not by being the first attachment.
        var link = document.QuerySelectorAll("a.file-download")
            .Select(a => (Href: a.GetAttribute("href"), Text: Collapse(a.TextContent)))
            .Where(a => a.Href is not null && a.Href.Contains("/attachment/", StringComparison.OrdinalIgnoreCase))
            .Where(a => a.Text.Contains(".xlsx", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(a => a.Text.Contains("lista sankcyjna", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();

        if (link.Href is null)
        {
            // No fallback URL exists to fall back to: the attachment address is the thing
            // being discovered. Failing here is what keeps the previous release in place.
            throw new PlMswiaParseException(
                $"The landing page {options.LandingPageUrl} offers no XLSX attachment. " +
                "The ministry has changed how the list is published.");
        }

        var url = new Uri(options.LandingPageUrl, link.Href);
        var attachmentId = url.Segments[^1].Trim('/');

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery"] = "landing-page",
            ["attachment.title"] = link.Text,
        };

        return new SourceDiscoveryResult(url, attachmentId, headers);
    }

    public async Task<DownloadResult> DownloadAsync(
        SourceDiscoveryResult discovery,
        ConditionalRequestState conditional,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(conditional);

        if (discovery.ExternalVersion is { Length: > 0 } attachmentId &&
            string.Equals(attachmentId, conditional.PreviousExternalVersion, StringComparison.OrdinalIgnoreCase))
        {
            // A new release is a new attachment. The same GUID is the same file, and no
            // request was made — status 0, not a 304 the server never sent.
            return new DownloadResult(
                Stream.Null, HttpStatus: 0, conditional.PreviousSha256 ?? string.Empty,
                ByteSize: 0, ContentType: null, ETag: null, LastModified: null)
            {
                Unchanged = true,
            };
        }

        using var response = await SendAsync(discovery.DownloadUrl, conditional, ct).ConfigureAwait(false);

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
            await RequireWorkbookAsync(content, byteSize, ct).ConfigureAwait(false);
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
        CancellationToken ct) => PlMswiaXlsxParser.ParseAsync(source, ct);

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.RecordCount < _guards.MinimumRecordCount)
        {
            return Task.FromResult(DatasetValidationResult.Reject(
                $"Parsed {stats.RecordCount} rows, fewer than the {_guards.MinimumRecordCount} required. " +
                "The list held 584 on 2026-10-15 and has only grown; a truncated download or a " +
                "page served in place of the workbook is far likelier than a list this size."));
        }

        if (stats.PreviousRecordCount is > 0 and var previous)
        {
            var shrink = (previous - stats.RecordCount) / (double)previous;
            if (shrink > _guards.MaxShrinkFraction)
            {
                return Task.FromResult(DatasetValidationResult.Reject(
                    $"Row count fell from {previous} to {stats.RecordCount} ({shrink:P1}), " +
                    $"beyond the {_guards.MaxShrinkFraction:P0} a single release may lose."));
            }
        }

        return Task.FromResult(DatasetValidationResult.Ok());
    }

    private async Task<HttpResponseMessage> SendAsync(Uri url, ConditionalRequestState? conditional, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", ingestOptions.UserAgent);

        // gov.pl serves attachments with a Last-Modified and a month-long max-age (seen
        // 2026-10-15), so an unchanged file answers 304 when the GUID comparison above
        // could not run — a first import after a restart, say.
        if (conditional?.ETag is { Length: > 0 } etag)
        {
            request.Headers.TryAddWithoutValidation("If-None-Match", etag);
        }
        else if (conditional?.LastModified is { Length: > 0 } lastModified)
        {
            request.Headers.TryAddWithoutValidation("If-Modified-Since", lastModified);
        }

        return await httpClient
            .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// An XLSX is a zip and starts with "PK". A gov.pl error page or a sign-in redirect
    /// served with 200 does not, and refusing it here gives an error that names the
    /// cause instead of a package exception from deep inside the reader.
    /// </summary>
    private static async Task RequireWorkbookAsync(Stream content, long byteSize, CancellationToken ct)
    {
        var magic = new byte[2];
        var read = await content.ReadAtLeastAsync(magic, 2, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        content.Position = 0;

        if (read < 2 || magic[0] != (byte)'P' || magic[1] != (byte)'K')
        {
            throw new PlMswiaParseException(
                $"The download ({byteSize} bytes) is not a workbook: it does not start with a zip header. " +
                "The server answered with a page instead of the file.");
        }
    }

    /// <summary>
    /// Spools to a temporary file, hashing as it goes: the package reader needs a seekable
    /// stream, and a network stream is not one. The file deletes itself on dispose.
    /// </summary>
    private static async Task<(Stream Content, string Sha256, long ByteSize)> BufferAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"sancmeld-plmswia-{Guid.NewGuid():N}.tmp"),
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

    /// <summary>
    /// gov.pl breaks long file names with zero-width spaces ("tabela&#8203;_lista…") so
    /// they wrap in a browser; they are not part of the name.
    /// </summary>
    private static string Collapse(string text) =>
        string.Join(' ', text.Replace("​", string.Empty, StringComparison.Ordinal)
            .Split((char[])[' ', '\t', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));
}
