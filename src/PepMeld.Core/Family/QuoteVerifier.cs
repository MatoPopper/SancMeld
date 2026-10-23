using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace PepMeld.Core.Family;

/// <summary>Whether a quote is on the page it cites.</summary>
public enum QuoteCheck
{
    /// <summary>Word for word, after folding case, whitespace and footnote marks.</summary>
    Exact,

    /// <summary>
    /// A run of at least half its words, and at least four: a quote copied across an
    /// infobox cell or an inline link is broken up on the page but still there.
    /// </summary>
    Partial,

    NotFound,

    /// <summary>The page could not be read (paywall, script-only page, timeout); nothing is known.</summary>
    Unreachable,
}

public interface IQuoteVerifier
{
    Task<QuoteCheck> CheckAsync(string url, string quote, CancellationToken ct);
}

/// <summary>
/// Fetches the cited page and looks for the quote in its text. The guard against an agent
/// that writes what it expects rather than what it read: measured on the first two runs
/// (2026-10-20), 22 of 25 quotes were on their page, and every relative had at least one
/// that was. Pages are fetched once per import.
/// </summary>
public sealed partial class HttpQuoteVerifier(IHttpClientFactory httpClients) : IQuoteVerifier
{
    public const string HttpClientName = "PepMeld.QuoteVerifier";

    private readonly Dictionary<string, string?> pages = new(StringComparer.Ordinal);

    // psp.cz and other Czech sites still serve windows-1250, which .NET reads only with the
    // code-page provider; without it reading the page throws and the import stops.
    static HttpQuoteVerifier() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    [GeneratedRegex(@"<(script|style)[^>]*>.*?</\1>", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex Scripts();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();

    [GeneratedRegex(@"\[\d+\]")]
    private static partial Regex Footnotes();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    /// <summary>A tag replaced by a space leaves one before the punctuation that followed it.</summary>
    [GeneratedRegex(@"\s+([.,;:!?)\]])")]
    private static partial Regex SpaceBeforePunctuation();

    public async Task<QuoteCheck> CheckAsync(string url, string quote, CancellationToken ct)
    {
        if (!pages.TryGetValue(url, out var text))
        {
            text = await FetchAsync(url, ct).ConfigureAwait(false);
            pages[url] = text;
        }

        return text is null ? QuoteCheck.Unreachable : Find(Fold(quote), text);
    }

    internal static QuoteCheck Find(string quote, string text)
    {
        if (quote.Length == 0)
        {
            return QuoteCheck.NotFound;
        }

        if (text.Contains(quote, StringComparison.Ordinal))
        {
            return QuoteCheck.Exact;
        }

        var words = quote.Split(' ');
        var floor = Math.Max(4, words.Length / 2);
        for (var size = words.Length - 1; size >= floor; size--)
        {
            for (var start = 0; start + size <= words.Length; start++)
            {
                if (text.Contains(string.Join(' ', words, start, size), StringComparison.Ordinal))
                {
                    return QuoteCheck.Partial;
                }
            }
        }

        return QuoteCheck.NotFound;
    }

    /// <summary>Lower case, NFC, typographic quotes and non-breaking spaces made plain, footnote marks and runs of whitespace gone.</summary>
    internal static string Fold(string text)
    {
        text = WebUtility.HtmlDecode(text).Normalize(NormalizationForm.FormC)
            .Replace(' ', ' ').Replace('’', '\'').Replace('„', '"').Replace('“', '"').Replace('”', '"');
        text = Footnotes().Replace(text, " ");
        text = Spaces().Replace(text, " ");
        return SpaceBeforePunctuation().Replace(text, "$1").Trim().ToLower(CultureInfo.InvariantCulture);
    }

    private async Task<string?> FetchAsync(string url, CancellationToken ct)
    {
        try
        {
            using var response = await httpClients.CreateClient(HttpClientName).GetAsync(new Uri(url), ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return null;
            }

            var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return Fold(Tags().Replace(Scripts().Replace(html, " "), " "));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException)
        {
            return null;
        }
    }
}

/// <summary>For tests and for an import that must not reach out: every quote is taken as found.</summary>
public sealed class TrustingQuoteVerifier : IQuoteVerifier
{
    public Task<QuoteCheck> CheckAsync(string url, string quote, CancellationToken ct) => Task.FromResult(QuoteCheck.Exact);
}
