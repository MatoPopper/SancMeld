using System.Globalization;
using System.Xml.Linq;
using AngleSharp.Html.Parser;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;

namespace HybridMeld.Core.Sources;

/// <summary>One entry a fetch found: its id in the source, where it is, what it says.</summary>
public sealed record FetchedItem(string ExternalId, string Url, string? Title, string Text, DateTimeOffset? PublishedAt, byte[] Raw);

/// <summary>Reads one kind of source into items. The fetch service picks the fetcher by the source's FetchKind.</summary>
public interface ISourceFetcher
{
    FetchKind Kind { get; }
    Task<IReadOnlyList<FetchedItem>> FetchAsync(Source source, CancellationToken ct);
}

/// <summary>RSS 2.0 and Atom, by hand with XDocument: the two shapes are small and the official feeds (Council, NÚKIB, EUvsDisinfo) are plain.</summary>
public sealed class RssFetcher(PageFetcher pages) : ISourceFetcher
{
    public FetchKind Kind => FetchKind.Rss;

    public async Task<IReadOnlyList<FetchedItem>> FetchAsync(Source source, CancellationToken ct)
    {
        var page = await pages.FetchAsync(new Uri(source.Url), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The feed is larger than a feed should be.");

        var xml = System.Text.Encoding.UTF8.GetString(page.Bytes);
        return Parse(xml);
    }

    public static IReadOnlyList<FetchedItem> Parse(string xml)
    {
        var document = XDocument.Parse(xml);
        var root = document.Root ?? throw new InvalidDataException("Empty feed.");
        var items = new List<FetchedItem>();

        if (root.Name.LocalName == "feed")
        {
            XNamespace atom = root.Name.Namespace;
            foreach (var entry in root.Elements(atom + "entry"))
            {
                var link = entry.Elements(atom + "link").FirstOrDefault(l => (string?)l.Attribute("rel") is null or "alternate")?.Attribute("href")?.Value
                    ?? entry.Elements(atom + "link").FirstOrDefault()?.Attribute("href")?.Value;
                var id = entry.Element(atom + "id")?.Value ?? link;
                if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(link))
                {
                    continue;
                }

                var title = entry.Element(atom + "title")?.Value.Trim();
                var text = StripHtml(entry.Element(atom + "content")?.Value ?? entry.Element(atom + "summary")?.Value ?? string.Empty);
                var when = ParseDate(entry.Element(atom + "published")?.Value ?? entry.Element(atom + "updated")?.Value);
                items.Add(new FetchedItem(id.Trim(), link.Trim(), title, text, when, System.Text.Encoding.UTF8.GetBytes(entry.ToString())));
            }

            return items;
        }

        var channel = root.Element("channel") ?? root;
        foreach (var item in channel.Elements("item"))
        {
            var link = item.Element("link")?.Value.Trim();
            var guid = item.Element("guid")?.Value.Trim();
            var id = string.IsNullOrWhiteSpace(guid) ? link : guid;
            if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(link))
            {
                continue;
            }

            var title = item.Element("title")?.Value.Trim();
            var contentEncoded = item.Elements().FirstOrDefault(e => e.Name.LocalName == "encoded")?.Value;
            var text = StripHtml(contentEncoded ?? item.Element("description")?.Value ?? string.Empty);
            var when = ParseDate(item.Element("pubDate")?.Value ?? item.Elements().FirstOrDefault(e => e.Name.LocalName == "date")?.Value);
            items.Add(new FetchedItem(id, link, title, text, when, System.Text.Encoding.UTF8.GetBytes(item.ToString())));
        }

        return items;
    }

    internal static string StripHtml(string html)
    {
        if (!html.Contains('<'))
        {
            return Fold(html);
        }

        var parser = new HtmlParser();
        using var fragment = parser.ParseDocument("<body>" + html + "</body>");
        return Fold(fragment.Body?.TextContent ?? string.Empty);
    }

    internal static string Fold(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    internal static DateTimeOffset? ParseDate(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var iso))
        {
            return iso;
        }

        // RFC 822 as feeds write it, "Wed, 21 Oct 2026 08:00:00 GMT" or "+0100". The day
        // name is dropped before parsing: a feed whose weekday disagrees with its date
        // (it happens, by hand-edited templates) still carries a usable date.
        var cleaned = value.Trim().Replace(" GMT", " +0000", StringComparison.Ordinal).Replace(" UT", " +0000", StringComparison.Ordinal);
        var comma = cleaned.IndexOf(',', StringComparison.Ordinal);
        if (comma is > 0 and <= 4)
        {
            cleaned = cleaned[(comma + 1)..].Trim();
        }

        return DateTimeOffset.TryParseExact(cleaned, ["dd MMM yyyy HH:mm:ss zzz", "d MMM yyyy HH:mm:ss zzz", "dd MMM yyyy HH:mm zzz"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var rfc)
            ? rfc
            : null;
    }
}

/// <summary>
/// A public Telegram channel through its web mirror, https://t.me/s/&lt;channel&gt;, which
/// shows the latest twenty or so posts with no account and no key. Enough for a watch:
/// an hourly fetch of a channel that posts less than twenty times an hour misses nothing.
/// The channel's name is the source URL's last segment, so "https://t.me/somechannel"
/// and "https://t.me/s/somechannel" both work.
/// </summary>
public sealed class TelegramFetcher(PageFetcher pages) : ISourceFetcher
{
    public FetchKind Kind => FetchKind.Telegram;

    public async Task<IReadOnlyList<FetchedItem>> FetchAsync(Source source, CancellationToken ct)
    {
        var channel = ChannelName(source.Url);
        var page = await pages.FetchAsync(new Uri($"https://t.me/s/{channel}"), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("Telegram answered with something that is not a page.");

        return Parse(System.Text.Encoding.UTF8.GetString(page.Bytes));
    }

    public static string ChannelName(string url)
    {
        var path = Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.AbsolutePath : url;
        var name = path.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? string.Empty;
        return name.TrimStart('@');
    }

    public static IReadOnlyList<FetchedItem> Parse(string html)
    {
        var parser = new HtmlParser();
        using var document = parser.ParseDocument(html);
        var items = new List<FetchedItem>();

        foreach (var message in document.QuerySelectorAll("div.tgme_widget_message"))
        {
            var post = message.GetAttribute("data-post");
            if (string.IsNullOrWhiteSpace(post))
            {
                continue;
            }

            var text = RssFetcher.Fold(message.QuerySelector(".tgme_widget_message_text")?.TextContent ?? string.Empty);
            var time = message.QuerySelector("time")?.GetAttribute("datetime");
            var when = RssFetcher.ParseDate(time);
            var url = "https://t.me/" + post;
            var title = text.Length > 120 ? text[..120] + "…" : text.Length > 0 ? text : null;
            items.Add(new FetchedItem(post, url, title, text, when, System.Text.Encoding.UTF8.GetBytes(message.OuterHtml)));
        }

        return items;
    }
}

/// <summary>
/// A plain web page watched as a whole: a ministry's warnings page, a list of reports.
/// One item per distinct content, keyed by its hash, so a page that changed is a new
/// item and one that did not is nothing.
/// </summary>
public sealed class HtmlFetcher(PageFetcher pages) : ISourceFetcher
{
    public FetchKind Kind => FetchKind.Html;

    public async Task<IReadOnlyList<FetchedItem>> FetchAsync(Source source, CancellationToken ct)
    {
        var page = await pages.FetchAsync(new Uri(source.Url), ct).ConfigureAwait(false)
            ?? throw new InvalidDataException("The source is not an HTML page.");

        var key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(page.Text)));
        return [new FetchedItem(key, source.Url, page.Title ?? source.Name, page.Text, null, page.Bytes)];
    }
}
