using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using AngleSharp.Html.Parser;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Ingest;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// Vnitrostátní sankční seznam — the Czech national sanctions list, kept by the Ministry
/// of Foreign Affairs under Act No. 1/2023 Coll. The government lists by resolution;
/// the ministry publishes.
///
/// Small and specific: 26 entries on 2026-10-15, each citing the EU act whose listing
/// criteria the subject met and the government resolution that listed them. Most are
/// people the EU had not yet listed — and the list says so when that changes: an entry
/// is "zrušen" with a note naming the Council decision that put the subject on the EU
/// list instead. Reading those notes is how the connector knows why a delisting
/// happened.
///
/// Advisory, like every national list here: the measures bind in the Czech Republic.
///
/// Published as a CSV on the ministry's open-data page since July 2026 (a PDF before
/// that), addressed by a file id and a date that change with every release. Discovery
/// reads the page; the file id is the version marker.
/// </summary>
public sealed partial class CzMzvConnector(
    HttpClient httpClient,
    CzMzvOptions options,
    IngestOptions ingestOptions) : ISanctionsConnector
{
    public string SourceCode => "CZ_MZV";

    private readonly IngestGuards _guards = new() { MinimumRecordCount = options.MinimumRecordCount };

    public async Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct)
    {
        using var response = await SendAsync(options.OpenDataPageUrl, conditional: null, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var html = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var document = await new HtmlParser().ParseDocumentAsync(html, ct).ConfigureAwait(false);

        // The page lists several open datasets; the list is the one whose file name says
        // so. Matched on the file name rather than the link title, which is prose.
        var link = document.QuerySelectorAll("a[href]")
            .Select(a => (Href: a.GetAttribute("href")!, Title: Collapse(a.GetAttribute("title") ?? a.TextContent)))
            .FirstOrDefault(a =>
                a.Href.Contains("Vnitrostatni_sankcni_seznam", StringComparison.OrdinalIgnoreCase)
                && a.Href.EndsWith(".csv", StringComparison.OrdinalIgnoreCase));

        if (link.Href is null)
        {
            // No address to fall back to: the file's address is the thing being
            // discovered. The previous release stays in place.
            throw new CzMzvParseException(
                $"The open-data page {options.OpenDataPageUrl} offers no CSV of the national sanctions list. " +
                "The ministry has changed how the list is published.");
        }

        var url = new Uri(options.OpenDataPageUrl, link.Href);
        var fileId = FileIdPattern().Match(url.AbsolutePath) is { Success: true } match
            ? match.Groups[1].Value
            : url.Segments[^1];

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["discovery"] = "open-data-page",
            ["file.name"] = url.Segments[^1],
            ["file.title"] = link.Title,
        };

        return new SourceDiscoveryResult(url, fileId, headers);
    }

    public async Task<DownloadResult> DownloadAsync(
        SourceDiscoveryResult discovery,
        ConditionalRequestState conditional,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(discovery);
        ArgumentNullException.ThrowIfNull(conditional);

        if (discovery.ExternalVersion is { Length: > 0 } fileId &&
            string.Equals(fileId, conditional.PreviousExternalVersion, StringComparison.Ordinal))
        {
            // A new release is a new file id. The same id is the same file, and no
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
            await RequireCsvAsync(content, byteSize, ct).ConfigureAwait(false);
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
        CancellationToken ct) => CzMzvCsvParser.ParseAsync(source, ct);

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(stats);

        if (stats.RecordCount < _guards.MinimumRecordCount)
        {
            return Task.FromResult(DatasetValidationResult.Reject(
                $"Parsed {stats.RecordCount} entries, fewer than the {_guards.MinimumRecordCount} required. " +
                "The list held 26 on 2026-10-15 and keeps its delisted entries, so it does not shrink; " +
                "a page served in place of the file is far likelier."));
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

    private async Task<HttpResponseMessage> SendAsync(Uri url, ConditionalRequestState? conditional, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", ingestOptions.UserAgent);

        // The file is served with a Last-Modified (seen 2026-10-15), so an unchanged
        // release answers 304 when the file-id comparison could not run.
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
    /// The file starts with its header, and the header starts with "Příjmení" (surname).
    /// An HTML page served with 200 — the ministry's site does that for a moved file —
    /// starts with a tag, and refusing it here names the cause.
    /// </summary>
    private static async Task RequireCsvAsync(Stream content, long byteSize, CancellationToken ct)
    {
        var head = new byte[64];
        var read = await content.ReadAtLeastAsync(head, 1, throwOnEndOfStream: false, ct).ConfigureAwait(false);
        content.Position = 0;

        var text = System.Text.Encoding.UTF8.GetString(head, 0, read).TrimStart('﻿', ' ', '\r', '\n', '"');

        if (!text.StartsWith("Příjmení", StringComparison.OrdinalIgnoreCase))
        {
            throw new CzMzvParseException(
                $"The download ({byteSize} bytes) does not start with the list's header; it begins '{text[..Math.Min(text.Length, 20)]}'. " +
                "The server answered with something other than the file, or the columns have changed.");
        }
    }

    /// <summary>
    /// Spools to a temporary file, hashing as it goes. 67 kB today would fit in memory,
    /// but the header check above and the parser both read from the start, and the
    /// other connectors do the same, so this one does too. The file deletes itself.
    /// </summary>
    private static async Task<(Stream Content, string Sha256, long ByteSize)> BufferAsync(
        HttpResponseMessage response,
        CancellationToken ct)
    {
        var file = new FileStream(
            Path.Combine(Path.GetTempPath(), $"sancmeld-czmzv-{Guid.NewGuid():N}.tmp"),
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

    private static string Collapse(string text) =>
        string.Join(' ', text.Split((char[])[' ', '\t', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex(@"/file/(\d+)/")]
    private static partial Regex FileIdPattern();
}
