using HybridMeld.Core.Sources;
using Xunit;

namespace HybridMeld.Core.Tests;

public class FetchersTests
{
    [Fact]
    public void Rss_items_have_id_link_title_text_and_date()
    {
        const string xml = """
            <?xml version="1.0"?>
            <rss version="2.0" xmlns:content="http://purl.org/rss/1.0/modules/content/">
              <channel>
                <title>Press</title>
                <item>
                  <title>Council lists eight more</title>
                  <link>https://example.eu/press/1</link>
                  <guid isPermaLink="false">press-1</guid>
                  <pubDate>Wed, 21 Oct 2026 08:00:00 GMT</pubDate>
                  <description><![CDATA[<p>The Council <b>listed</b> eight persons.</p>]]></description>
                </item>
                <item>
                  <title>No link</title>
                </item>
              </channel>
            </rss>
            """;

        var items = RssFetcher.Parse(xml);

        var item = Assert.Single(items);
        Assert.Equal("press-1", item.ExternalId);
        Assert.Equal("https://example.eu/press/1", item.Url);
        Assert.Equal("Council lists eight more", item.Title);
        Assert.Equal("The Council listed eight persons.", item.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 8, 0, 0, TimeSpan.Zero), item.PublishedAt);
    }

    [Fact]
    public void Atom_entries_use_alternate_link_and_published_date()
    {
        const string xml = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
              <entry>
                <id>urn:x:42</id>
                <title>Warning</title>
                <link rel="self" href="https://example.cz/feed/42"/>
                <link rel="alternate" href="https://example.cz/warnings/42"/>
                <published>2026-10-20T06:30:00+02:00</published>
                <summary>Phishing wave.</summary>
              </entry>
            </feed>
            """;

        var item = Assert.Single(RssFetcher.Parse(xml));

        Assert.Equal("urn:x:42", item.ExternalId);
        Assert.Equal("https://example.cz/warnings/42", item.Url);
        Assert.Equal("Phishing wave.", item.Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 4, 30, 0, TimeSpan.Zero), item.PublishedAt);
    }

    [Fact]
    public void Telegram_page_yields_one_item_per_post_with_its_permalink()
    {
        const string html = """
            <html><body>
              <div class="tgme_widget_message_wrap">
                <div class="tgme_widget_message" data-post="somechannel/101">
                  <div class="tgme_widget_message_text">First   post <a href="x">with a link</a>.</div>
                  <time datetime="2026-10-21T07:15:00+00:00">07:15</time>
                </div>
              </div>
              <div class="tgme_widget_message" data-post="somechannel/102">
                <div class="tgme_widget_message_text">Second post.</div>
              </div>
              <div class="tgme_widget_message"></div>
            </body></html>
            """;

        var items = TelegramFetcher.Parse(html);

        Assert.Equal(2, items.Count);
        Assert.Equal("somechannel/101", items[0].ExternalId);
        Assert.Equal("https://t.me/somechannel/101", items[0].Url);
        Assert.Equal("First post with a link.", items[0].Text);
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 7, 15, 0, TimeSpan.Zero), items[0].PublishedAt);
        Assert.Null(items[1].PublishedAt);
    }

    [Theory]
    [InlineData("https://t.me/somechannel", "somechannel")]
    [InlineData("https://t.me/s/somechannel", "somechannel")]
    [InlineData("https://t.me/s/somechannel/", "somechannel")]
    [InlineData("@somechannel", "somechannel")]
    public void Telegram_channel_name_is_the_last_path_segment(string url, string expected) =>
        Assert.Equal(expected, TelegramFetcher.ChannelName(url));

    [Fact]
    public void Page_text_drops_scripts_and_folds_whitespace()
    {
        var (title, text) = PageFetcher.ExtractText("<html><head><title> A page </title><script>x()</script></head><body><nav>menu</nav><p>Hello\n\n  world</p></body></html>", 100);

        Assert.Equal("A page", title);
        Assert.Equal("Hello world", text);
    }
}
