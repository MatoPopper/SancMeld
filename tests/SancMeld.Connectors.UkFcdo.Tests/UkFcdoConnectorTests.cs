using System.Net;
using System.Text;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using Xunit;

namespace SancMeld.Connectors.UkFcdo.Tests;

public class UkFcdoConnectorTests
{
    private static UkFcdoConnector Connector(StubHandler handler) =>
        new(new HttpClient(handler), new UkFcdoOptions(), new IngestOptions());

    [Fact]
    public async Task DiscoveryIsTheConfiguredAddressAndFetchesNothing()
    {
        var handler = new StubHandler(_ => throw new InvalidOperationException("not used"));

        var discovery = await Connector(handler).DiscoverAsync(CancellationToken.None);

        Assert.EndsWith("/UK-Sanctions-List.xml", discovery.DownloadUrl.AbsolutePath, StringComparison.Ordinal);
        Assert.Null(discovery.ExternalVersion);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task AnUnchangedListIsA304WithTheOldChecksum()
    {
        var handler = new StubHandler(request =>
        {
            Assert.Equal("\"0e7057662b826e5944304cc69e563676\"", request.Headers.IfNoneMatch.ToString());
            return new HttpResponseMessage(HttpStatusCode.NotModified);
        });

        var connector = Connector(handler);
        var discovery = await connector.DiscoverAsync(CancellationToken.None);
        var conditional = new ConditionalRequestState("\"0e7057662b826e5944304cc69e563676\"", null, "previous-sha256");

        var result = await connector.DownloadAsync(discovery, conditional, CancellationToken.None);

        Assert.True(result.Unchanged);
        Assert.Equal(304, result.HttpStatus);
        Assert.Equal("previous-sha256", result.Sha256);
    }

    [Fact]
    public async Task APageServedInPlaceOfTheListIsRefused()
    {
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("<html><body>Access denied</body></html>", Encoding.UTF8, "text/html"),
        });

        var connector = Connector(handler);
        var discovery = await connector.DiscoverAsync(CancellationToken.None);

        var failure = await Assert.ThrowsAsync<UkFcdoParseException>(() =>
            connector.DownloadAsync(discovery, new ConditionalRequestState(null, null, null), CancellationToken.None));

        Assert.Contains("<Designations>", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheRealDocumentPassesTheCheckAndIsHashed()
    {
        var bytes = await File.ReadAllBytesAsync("fixtures/uk-fcdo/sample.xml");
        var handler = new StubHandler(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(bytes) { Headers = { ContentType = new("text/xml") } },
        });

        var connector = Connector(handler);
        var discovery = await connector.DiscoverAsync(CancellationToken.None);
        var result = await connector.DownloadAsync(discovery, new ConditionalRequestState(null, null, null), CancellationToken.None);

        Assert.Equal(bytes.Length, result.ByteSize);
        Assert.Equal(64, result.Sha256.Length);
        Assert.Contains("SancMeld", handler.Requests.Single().Headers.UserAgent.ToString(), StringComparison.Ordinal);
        await result.Content.DisposeAsync();
    }

    [Fact]
    public async Task AShortListIsRejectedBeforePublication()
    {
        var connector = Connector(new StubHandler(_ => throw new InvalidOperationException("not used")));

        Assert.False((await connector.ValidateAsync(new ParsedDatasetStatistics(12, 0, 6408), CancellationToken.None)).IsAcceptable);
        Assert.False((await connector.ValidateAsync(new ParsedDatasetStatistics(4500, 0, 6408), CancellationToken.None)).IsAcceptable);
        Assert.True((await connector.ValidateAsync(new ParsedDatasetStatistics(6300, 0, 6408), CancellationToken.None)).IsAcceptable);
    }

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
