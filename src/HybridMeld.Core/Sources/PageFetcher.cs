using System.Net.Http.Headers;
using AngleSharp.Html.Parser;
using HybridMeld.Core.Configuration;
using Microsoft.Extensions.Options;

namespace HybridMeld.Core.Sources;

/// <summary>One fetched page: its bytes as served, and what the HTML says it is called.</summary>
public sealed record FetchedPage(byte[] Bytes, string? ContentType, string? Title, string Text);

/// <summary>
/// Fetches a page for a snapshot. Not a crawler: one URL, one request, a size cap, and
/// null rather than an exception when the server answers with something that is not a
/// page (a PDF, an image), which the caller then keeps as is.
/// </summary>
public sealed class PageFetcher(IHttpClientFactory clients, IOptions<HybridMeldOptions> options)
{
    public const string HttpClientName = "HybridMeld.Pages";

    /// <summary>Four megabytes: a news page with its scripts is under one; anything larger is not a text we quote.</summary>
    private const int MaxBytes = 4 * 1024 * 1024;

    public async Task<FetchedPage?> FetchAsync(Uri url, CancellationToken ct)
    {
        var client = clients.CreateClient(HttpClientName);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/html"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/xhtml+xml"));
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("*/*", 0.5));

        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is > MaxBytes)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var buffer = new MemoryStream();
        await CopyCappedAsync(stream, buffer, ct).ConfigureAwait(false);
        var bytes = buffer.ToArray();

        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not null && !contentType.Contains("html", StringComparison.OrdinalIgnoreCase) && !contentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
        {
            return new FetchedPage(bytes, contentType, null, string.Empty);
        }

        var html = System.Text.Encoding.UTF8.GetString(bytes);
        var (title, text) = ExtractText(html, options.Value.Ingest.MaxTextLength);
        return new FetchedPage(bytes, contentType, title, text);
    }

    /// <summary>The title and the visible text of a page, scripts and styles dropped, whitespace folded.</summary>
    public static (string? Title, string Text) ExtractText(string html, int maxText)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        foreach (var element in document.QuerySelectorAll("script, style, noscript, svg, nav, footer, header").ToList())
        {
            element.Remove();
        }

        var title = document.Title?.Trim();
        var text = string.Join(' ', (document.Body?.TextContent ?? string.Empty).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return (string.IsNullOrEmpty(title) ? null : title, text.Length <= maxText ? text : text[..maxText]);
    }

    private static async Task CopyCappedAsync(Stream from, Stream to, CancellationToken ct)
    {
        var chunk = new byte[64 * 1024];
        var total = 0;
        int read;
        while ((read = await from.ReadAsync(chunk, ct).ConfigureAwait(false)) > 0)
        {
            total += read;
            if (total > MaxBytes)
            {
                throw new IOException($"The page is larger than {MaxBytes / 1024 / 1024} MB.");
            }

            await to.WriteAsync(chunk.AsMemory(0, read), ct).ConfigureAwait(false);
        }
    }
}
