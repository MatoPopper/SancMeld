using System.Net;
using System.Text;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using Xunit;

namespace SancMeld.Connectors.EuFsf.Tests;

public class EuFsfConnectorTests
{
    /// <summary>
    /// Trimmed from the live feed. The two XML entries matter: their titles differ only
    /// in prose ("v1.1" / "v1.0") while the path is what actually identifies the file.
    /// </summary>
    private const string Rss = """
        <?xml version="1.0" encoding="UTF-8"?>
        <rss version="2.0">
          <channel>
            <lastBuildDate>Thu, 06 Aug 2026 15:12:34 GMT</lastBuildDate>
            <item>
              <title>XML (Based on XSD) - v1.0</title>
              <enclosure url="https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList/content?token=dG9rZW4tMjAxNw" length="25571912" type="application/xml" />
              <pubDate>Wed, 05 Aug 2026 14:50:09 GMT</pubDate>
              <guid>https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList/content?token=dG9rZW4tMjAxNw&amp;checksum=EA64655C96795445EAD6497778D968D3399AC6BB</guid>
            </item>
            <item>
              <title>XML (Based on XSD) - v1.1</title>
              <enclosure url="https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList_1_1/content?token=dG9rZW4tMjAxNw" length="25766640" type="application/xml" />
              <pubDate>Wed, 05 Aug 2026 14:50:10 GMT</pubDate>
              <guid>https://webgate.ec.europa.eu/fsd/fsf/public/files/xmlFullSanctionsList_1_1/content?token=dG9rZW4tMjAxNw&amp;checksum=3FA2B33658BC71E23DCDDDDDC4E865A0AA1AA51D</guid>
            </item>
          </channel>
        </rss>
        """;

    private const string XmlV11Checksum = "3FA2B33658BC71E23DCDDDDDC4E865A0AA1AA51D";

    private static EuFsfConnector Connector(StubHandler handler, EuFsfOptions? options = null) =>
        new(new HttpClient(handler), options ?? new EuFsfOptions(), new IngestOptions());

    [Fact]
    public async Task DiscoveryTakesTheVersionFromTheFeedRatherThanDownloading()
    {
        var handler = new StubHandler(_ => Respond(Rss, "application/rss+xml"));

        var discovery = await Connector(handler).DiscoverAsync(CancellationToken.None);

        Assert.Equal(XmlV11Checksum, discovery.ExternalVersion);
        Assert.Contains("xmlFullSanctionsList_1_1", discovery.DownloadUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.Equal("rss", discovery.Headers!["discovery"]);
        Assert.Equal("25766640", discovery.Headers["rss.length"]);

        // 3.9 kB, not 25 MB: nothing but the feed was fetched.
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DiscoveryMatchesTheFilePathNotTheItemTitle()
    {
        // Both XML items are titled "XML (Based on XSD)". Selecting on the title would be
        // a coin toss between the 1.0 and 1.1 schemas.
        var handler = new StubHandler(_ => Respond(Rss, "application/rss+xml"));
        var options = new EuFsfOptions { FileVariant = "xmlFullSanctionsList" };

        var discovery = await Connector(handler, options).DiscoverAsync(CancellationToken.None);

        Assert.Equal("EA64655C96795445EAD6497778D968D3399AC6BB", discovery.ExternalVersion);
        Assert.EndsWith("/xmlFullSanctionsList/content", discovery.DownloadUrl.AbsolutePath, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DiscoveryUsesTheConfiguredTokenNotTheOneInTheFeed()
    {
        var handler = new StubHandler(_ => Respond(Rss, "application/rss+xml"));
        var options = new EuFsfOptions { Token = "deployment-specific" };

        var discovery = await Connector(handler, options).DiscoverAsync(CancellationToken.None);

        Assert.Equal("?token=deployment-specific", discovery.DownloadUrl.Query);
    }

    [Fact]
    public async Task LosingTheFeedDegradesToADirectDownloadInsteadOfFailing()
    {
        // The feed is an optimisation. A sanctions list that cannot be fetched because a
        // 4 kB convenience file is down would be a self-inflicted compliance gap.
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.InternalServerError));

        var discovery = await Connector(handler).DiscoverAsync(CancellationToken.None);

        Assert.Null(discovery.ExternalVersion);
        Assert.Equal("fallback", discovery.Headers!["discovery"]);
        Assert.Contains("discovery.reason", discovery.Headers.Keys);
        Assert.Contains("xmlFullSanctionsList_1_1", discovery.DownloadUrl.AbsoluteUri, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UnchangedReleaseIsNotDownloadedAgain()
    {
        var handler = new StubHandler(_ => Respond(Rss, "application/rss+xml"));
        var connector = Connector(handler);

        var discovery = await connector.DiscoverAsync(CancellationToken.None);
        var conditional = new ConditionalRequestState(null, null, "previous-sha256")
        {
            PreviousExternalVersion = XmlV11Checksum,
        };

        var result = await connector.DownloadAsync(discovery, conditional, CancellationToken.None);

        Assert.True(result.Unchanged);
        Assert.Equal(0, result.HttpStatus);
        Assert.Single(handler.Requests);   // still only the feed
    }

    [Fact]
    public async Task PayloadThatDoesNotMatchThePublishedChecksumIsRefused()
    {
        // The advertised SHA-1 is the only thing that distinguishes a truncated transfer
        // from a short release. Interpreting the bytes first would risk publishing it.
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith("rss", StringComparison.Ordinal)
                ? Respond(Rss, "application/rss+xml")
                : Respond("<export/>", "application/xml"));

        var connector = Connector(handler);
        var discovery = await connector.DiscoverAsync(CancellationToken.None);

        var failure = await Assert.ThrowsAsync<EuFsfParseException>(() =>
            connector.DownloadAsync(discovery, new ConditionalRequestState(null, null, null), CancellationToken.None));

        Assert.Contains(XmlV11Checksum, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task DownloadHashesAndSizesWhatItActuallyReceived()
    {
        const string body = "<export/>";
        var handler = new StubHandler(_ => Respond(body, "application/xml"));
        var connector = Connector(handler);

        // No advertised checksum to compare against, which is the fallback-discovery case.
        var discovery = new SourceDiscoveryResult(new Uri("https://example.invalid/x"), null);

        var result = await connector.DownloadAsync(discovery, new ConditionalRequestState(null, null, null), CancellationToken.None);

        await using (result.Content)
        {
            Assert.False(result.Unchanged);
            Assert.Equal(200, result.HttpStatus);
            Assert.Equal(Encoding.UTF8.GetByteCount(body), result.ByteSize);
            Assert.Equal(
                Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(body))).ToLowerInvariant(),
                result.Sha256);

            // Readable from the start: the parser gets the whole document, not the tail.
            Assert.Equal(0, result.Content.Position);
        }
    }

    [Theory]
    [InlineData(6234, 0, 6200, true)]        // an ordinary release
    [InlineData(3999, 0, null, false)]       // below the floor: a truncated file, not a shrunken list
    [InlineData(4000, 0, null, true)]        // exactly the floor is acceptable
    [InlineData(4500, 0, 6234, false)]       // a 27 % drop in one release
    [InlineData(6000, 0, 6234, true)]        // a 4 % drop is normal churn
    [InlineData(6000, 500, 6234, false)]     // 7.7 % of records unparseable
    public async Task GuardsRefuseTheReleasesThatCouldEmptyTheList(
        int recordCount, int parseErrors, int? previousCount, bool acceptable)
    {
        var connector = Connector(new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound)));

        var result = await connector.ValidateAsync(
            new ParsedDatasetStatistics(recordCount, parseErrors, previousCount), CancellationToken.None);

        Assert.Equal(acceptable, result.IsAcceptable);
        Assert.Equal(acceptable, result.Reason is null);
    }

    private static HttpResponseMessage Respond(string body, string mediaType) =>
        new(HttpStatusCode.OK) { Content = new StringContent(body, Encoding.UTF8, mediaType) };

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public List<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }
}
