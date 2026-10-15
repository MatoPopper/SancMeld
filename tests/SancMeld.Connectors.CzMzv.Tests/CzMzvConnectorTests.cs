using System.Net;
using System.Text;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using Xunit;

namespace SancMeld.Connectors.CzMzv.Tests;

public class CzMzvConnectorTests
{
    /// <summary>Trimmed from the ministry's open-data page of 2026-10-15.</summary>
    private const string OpenDataPage = """
        <!DOCTYPE html>
        <html lang="cs"><body>
        <h2>Otevřená data</h2>
        <a class="default" href="/public/b3/da/2f/4602428_2837911_Agenda_CBT_25_April_2022.pdf" title="Agenda">Agenda CBT</a>
        <h3>Vnitrostátní sankční seznam</h3>
        <a class="default" href="/file/6248997/Vnitrostatni_sankcni_seznam_2026_07_23.csv" title="Vnitrostátní sankční seznam">
        Vnitrostátní sankční seznam - 65&nbsp;kB csv (Dokument) 24. 7. 2026
        </a>
        </body></html>
        """;

    private const string Header = "\"Příjmení fyzické osoby/název právnické osoby/označení nebo název entity \",\"Jméno fyzické osoby \",\"Stav zápisu\"\r\n";

    private static CzMzvConnector Connector(StubHandler handler, CzMzvOptions? options = null) =>
        new(new HttpClient(handler), options ?? new CzMzvOptions(), new IngestOptions());

    [Fact]
    public async Task DiscoveryFindsTheCsvOnTheOpenDataPage()
    {
        var handler = new StubHandler(_ => Respond(OpenDataPage, "text/html"));

        var discovery = await Connector(handler).DiscoverAsync(CancellationToken.None);

        Assert.Equal("https://mzv.gov.cz/file/6248997/Vnitrostatni_sankcni_seznam_2026_07_23.csv", discovery.DownloadUrl.AbsoluteUri);
        Assert.Equal("6248997", discovery.ExternalVersion);
        Assert.Equal("Vnitrostatni_sankcni_seznam_2026_07_23.csv", discovery.Headers!["file.name"]);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DiscoveryFailsLoudlyWhenThePageOffersNoCsv()
    {
        var handler = new StubHandler(_ => Respond(OpenDataPage.Replace(".csv", ".pdf", StringComparison.Ordinal), "text/html"));

        await Assert.ThrowsAsync<CzMzvParseException>(() => Connector(handler).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnchangedFileIsNotDownloadedAgain()
    {
        var handler = new StubHandler(_ => Respond(OpenDataPage, "text/html"));
        var connector = Connector(handler);

        var discovery = await connector.DiscoverAsync(CancellationToken.None);
        var conditional = new ConditionalRequestState(null, null, "previous-sha256") { PreviousExternalVersion = "6248997" };

        var result = await connector.DownloadAsync(discovery, conditional, CancellationToken.None);

        Assert.True(result.Unchanged);
        Assert.Equal(0, result.HttpStatus);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task AFileWithoutTheListsHeaderIsRefused()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".csv", StringComparison.Ordinal)
                ? Respond("<html><body>Soubor nenalezen</body></html>", "text/html")
                : Respond(OpenDataPage, "text/html"));

        var connector = Connector(handler);
        var discovery = await connector.DiscoverAsync(CancellationToken.None);

        var failure = await Assert.ThrowsAsync<CzMzvParseException>(() =>
            connector.DownloadAsync(discovery, new ConditionalRequestState(null, null, null), CancellationToken.None));

        Assert.Contains("header", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheListsOwnHeaderPassesTheCheck()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.EndsWith(".csv", StringComparison.Ordinal)
                ? Respond(Header, "text/csv")
                : Respond(OpenDataPage, "text/html"));

        var connector = Connector(handler);
        var discovery = await connector.DiscoverAsync(CancellationToken.None);
        var result = await connector.DownloadAsync(discovery, new ConditionalRequestState(null, null, null), CancellationToken.None);

        Assert.False(result.Unchanged);
        Assert.Equal(200, result.HttpStatus);
        Assert.Equal(Encoding.UTF8.GetByteCount(Header), result.ByteSize);
        await result.Content.DisposeAsync();
    }

    [Fact]
    public async Task AShortListIsRejectedBeforePublication()
    {
        var connector = Connector(new StubHandler(_ => throw new InvalidOperationException("not used")));

        Assert.False((await connector.ValidateAsync(new ParsedDatasetStatistics(3, 0, 23), CancellationToken.None)).IsAcceptable);
        Assert.False((await connector.ValidateAsync(new ParsedDatasetStatistics(15, 0, 23), CancellationToken.None)).IsAcceptable);
        Assert.True((await connector.ValidateAsync(new ParsedDatasetStatistics(24, 0, 23), CancellationToken.None)).IsAcceptable);
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
