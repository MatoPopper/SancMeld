using System.Net;
using System.Text;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using Xunit;

namespace SancMeld.Connectors.PlMswia.Tests;

public class PlMswiaConnectorTests
{
    /// <summary>
    /// Trimmed from the live page of 2026-10-15. Two attachments matter: a decision PDF
    /// and the workbook, whose file name gov.pl breaks with zero-width spaces.
    /// </summary>
    private const string LandingPage = """
        <!DOCTYPE html>
        <html lang="pl"><body>
        <h2>Lista osób i podmiotów objętych sankcjami</h2>
        <a class="file-download" href="/attachment/09caa539-81a6-4d5e-b25b-3a28216c0ca8" target="_blank" download
        aria-label="Pobierz plik Decyzja MASHENSKY Rozmiar: 0.42MB Format: pdf">
        Decyzja MASHENSKY<br/>
        <span class="extension">Decyzja&#8203;_MASHENSKY.pdf</span>
        <span class="details">0.42MB</span>
        </a>
        <h3>Materiały</h3>
        <a class="file-download" href="/attachment/efe8ad28-0dd8-454b-b0ab-ea58033aa902" target="_blank" download
        aria-label="Pobierz plik Lista sankcyjna - tabela 26.05.2026r Rozmiar: 0.19MB Format: xlsx">
        Lista sankcyjna - tabela 26.05.2026r<br/>
        <span class="extension">tabela&#8203;_lista&#8203;_sankcyjna&#8203;_26&#8203;_05&#8203;_2026.xlsx</span>
        <span class="details">0.19MB</span>
        </a>
        </body></html>
        """;

    private const string AttachmentId = "efe8ad28-0dd8-454b-b0ab-ea58033aa902";

    private static PlMswiaConnector Connector(StubHandler handler, PlMswiaOptions? options = null) =>
        new(new HttpClient(handler), options ?? new PlMswiaOptions(), new IngestOptions());

    [Fact]
    public async Task DiscoveryFindsTheWorkbookAmongThePageAttachments()
    {
        var handler = new StubHandler(_ => Respond(LandingPage, "text/html"));

        var discovery = await Connector(handler).DiscoverAsync(CancellationToken.None);

        Assert.Equal($"https://www.gov.pl/attachment/{AttachmentId}", discovery.DownloadUrl.AbsoluteUri);
        Assert.Equal(AttachmentId, discovery.ExternalVersion);
        Assert.Equal("landing-page", discovery.Headers!["discovery"]);
        Assert.Contains("tabela_lista_sankcyjna_26_05_2026.xlsx", discovery.Headers["attachment.title"]);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public async Task DiscoveryFailsLoudlyWhenThePageOffersNoWorkbook()
    {
        // There is no address to fall back to: the attachment's address is the thing being
        // discovered. The previous release stays in place, which is the right outcome.
        var handler = new StubHandler(_ => Respond(LandingPage.Replace(".xlsx", ".pdf", StringComparison.Ordinal), "text/html"));

        await Assert.ThrowsAsync<PlMswiaParseException>(() => Connector(handler).DiscoverAsync(CancellationToken.None));
    }

    [Fact]
    public async Task UnchangedAttachmentIsNotDownloadedAgain()
    {
        var handler = new StubHandler(_ => Respond(LandingPage, "text/html"));
        var connector = Connector(handler);

        var discovery = await connector.DiscoverAsync(CancellationToken.None);
        var conditional = new ConditionalRequestState(null, null, "previous-sha256")
        {
            PreviousExternalVersion = AttachmentId,
        };

        var result = await connector.DownloadAsync(discovery, conditional, CancellationToken.None);

        Assert.True(result.Unchanged);
        Assert.Equal(0, result.HttpStatus);
        Assert.Single(handler.Requests);   // the page, nothing else
    }

    [Fact]
    public async Task APageServedInPlaceOfTheWorkbookIsRefused()
    {
        var handler = new StubHandler(request =>
            request.RequestUri!.AbsolutePath.Contains("/attachment/", StringComparison.Ordinal)
                ? Respond("<html><body>Przekierowanie</body></html>", "text/html")
                : Respond(LandingPage, "text/html"));

        var connector = Connector(handler);
        var discovery = await connector.DiscoverAsync(CancellationToken.None);

        var failure = await Assert.ThrowsAsync<PlMswiaParseException>(() =>
            connector.DownloadAsync(discovery, new ConditionalRequestState(null, null, null), CancellationToken.None));

        Assert.Contains("not a workbook", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EveryRequestCarriesTheUserAgent()
    {
        var handler = new StubHandler(_ => Respond(LandingPage, "text/html"));

        await Connector(handler).DiscoverAsync(CancellationToken.None);

        Assert.All(handler.Requests, r => Assert.Contains("SancMeld", r.Headers.UserAgent.ToString(), StringComparison.Ordinal));
    }

    [Fact]
    public async Task AShortListIsRejectedBeforePublication()
    {
        var connector = Connector(new StubHandler(_ => throw new InvalidOperationException("not used")));

        var tooFew = await connector.ValidateAsync(new ParsedDatasetStatistics(12, 0, 584), CancellationToken.None);
        Assert.False(tooFew.IsAcceptable);

        var shrunk = await connector.ValidateAsync(new ParsedDatasetStatistics(420, 0, 584), CancellationToken.None);
        Assert.False(shrunk.IsAcceptable);

        var fine = await connector.ValidateAsync(new ParsedDatasetStatistics(590, 0, 584), CancellationToken.None);
        Assert.True(fine.IsAcceptable);
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
