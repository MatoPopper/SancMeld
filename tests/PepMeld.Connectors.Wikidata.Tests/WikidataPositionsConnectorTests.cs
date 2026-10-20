using System.Net;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Connectors.Wikidata.Tests;

public class WikidataPositionsConnectorTests
{
    private const string Fixtures = "fixtures/cz-wikidata";

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            var query = Uri.UnescapeDataString(request.RequestUri.Query);
            var item = System.Text.RegularExpressions.Regex.Match(query, @"wd:(Q\d+)").Groups[1].Value;
            var path = Path.Combine(Fixtures, $"positions-{item}.json");

            return Task.FromResult(File.Exists(path)
                ? new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(path), System.Text.Encoding.UTF8, "application/sparql-results+json") }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (WikidataPositionsConnector Connector, FixtureHandler Handler) Connector()
    {
        var handler = new FixtureHandler();
        var connector = new WikidataPositionsConnector(
            new Factory(handler),
            Options.Create(new WikidataPositionsOptions { DelayMilliseconds = 0 }),
            TimeProvider.System,
            NullLogger<WikidataPositionsConnector>.Instance);
        return (connector, handler);
    }

    private static async Task<IReadOnlyList<ParsedPerson>> CrawlAndParseAsync(WikidataPositionsConnector connector)
    {
        using var download = await connector.DownloadAsync(CancellationToken.None);
        return connector.Parse(download.Content);
    }

    [Fact]
    public async Task OneQueryPerPositionAndOnePersonPerItem()
    {
        var (connector, handler) = Connector();
        var persons = await CrawlAndParseAsync(connector);

        Assert.Single(handler.Requests);
        Assert.Contains("query=", handler.Requests[0], StringComparison.Ordinal);
        Assert.Equal(5, persons.Count);
        Assert.All(persons, p => Assert.StartsWith("Q", p.SourceKey, StringComparison.Ordinal));
        Assert.All(persons, p => Assert.NotNull(p.BirthDate));
    }

    [Fact]
    public async Task TwoTermsAreTwoFunctionsOfOnePerson()
    {
        var (connector, _) = Connector();
        var vystrcil = (await CrawlAndParseAsync(connector)).Single(p => p.SourceKey == "Q12037619");

        Assert.Equal("Miloš", vystrcil.GivenName);
        Assert.Equal("Vystrčil", vystrcil.FamilyName);
        Assert.Equal(new DateOnly(1960, 8, 10), vystrcil.BirthDate);
        Assert.Equal("https://www.wikidata.org/wiki/Q12037619", vystrcil.SourceUrl);

        Assert.Equal(2, vystrcil.Functions.Count);
        Assert.All(vystrcil.Functions, f => Assert.Equal(PepCategory.MemberOfParliament, f.Category));
        Assert.All(vystrcil.Functions, f => Assert.Equal("senátor", f.Role));
        Assert.All(vystrcil.Functions, f => Assert.Equal("Senát Parlamentu ČR", f.Institution));
        Assert.All(vystrcil.Functions, f => Assert.Contains("Wikidata", f.DateNote));

        Assert.Contains(vystrcil.Functions, f => f.From == new DateOnly(2016, 10, 15) && f.To is null);
        Assert.Contains(vystrcil.Functions, f => f.From == new DateOnly(2010, 10, 23) && f.To == new DateOnly(2016, 10, 23));
        Assert.Equal(2, vystrcil.Functions.Select(f => f.SourceKey).Distinct().Count());
    }

    [Fact]
    public async Task TheNamesakeOfThePrimeMinisterIsAnotherItemWithAnotherBirthday()
    {
        var (connector, _) = Connector();
        var fiala = (await CrawlAndParseAsync(connector)).Single(p => p.DisplayName == "Petr Fiala");

        Assert.Equal("Q104123076", fiala.SourceKey);
        Assert.Equal(new DateOnly(1968, 11, 24), fiala.BirthDate);
    }

    [Theory]
    [InlineData("Miloš Vystrčil", "Miloš", "Vystrčil")]
    [InlineData("Jiří Oberfalzer", "Jiří", "Oberfalzer")]
    [InlineData("Anna Marie Nováková", "Anna Marie", "Nováková")]
    [InlineData("Madonna", "", "Madonna")]
    public void ALabelSplitsIntoGivenAndFamilyName(string label, string given, string family)
        => Assert.Equal((given, family), WikidataPositionsConnector.SplitLabel(label));

    [Fact]
    public void TheQueryAsksForThePrecisionOfTheBirthday()
    {
        var query = WikidataPositionsConnector.Query("Q18941264");

        Assert.Contains("wd:Q18941264", query);
        Assert.Contains("wikibase:timePrecision", query);
        Assert.Contains("pq:P580", query);
        Assert.Contains("pq:P582", query);
    }
}
