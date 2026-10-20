using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Connectors.SkNrsr.Tests;

/// <summary>
/// The whole connector against the saved pages: an HTTP handler serves fixtures/sk-nrsr
/// by URL, the connector crawls two terms into its zip, and Parse reads it back. The
/// handler also counts requests, which is how the profile cache is checked.
/// </summary>
public class SkNrsrConnectorTests
{
    private const string Fixtures = "fixtures/sk-nrsr";

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public List<string> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            Requests.Add(url);

            var list = Regex.Match(url, @"zoznam_abc.*CisObdobia=(\d+)");
            var profile = Regex.Match(url, @"PoslanecID=(\d+)");

            var file = list.Success ? $"list-{list.Groups[1].Value}.html"
                : profile.Success ? $"profile-{profile.Groups[1].Value}.html"
                : null;

            var path = file is null ? null : Path.Combine(Fixtures, file);
            if (path is null || !File.Exists(path))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(path), System.Text.Encoding.UTF8, "text/html") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (SkNrsrConnector Connector, FixtureHandler Handler, string Snapshots) Connector(int cacheDays = 0)
    {
        var handler = new FixtureHandler();
        var snapshots = Path.Combine(Path.GetTempPath(), "pepmeld-nrsr-tests", Guid.NewGuid().ToString("N"));
        var connector = new SkNrsrConnector(
            new Factory(handler),
            Options.Create(new SkNrsrOptions { Terms = "8-9", DelayMilliseconds = 0, ProfileCacheDays = cacheDays }),
            Options.Create(new PepMeldOptions { Ingest = new IngestOptions { SnapshotStoragePath = snapshots } }),
            TimeProvider.System,
            NullLogger<SkNrsrConnector>.Instance);
        return (connector, handler, snapshots);
    }

    private static async Task<IReadOnlyList<ParsedPerson>> CrawlAndParseAsync(SkNrsrConnector connector)
    {
        using var download = await connector.DownloadAsync(CancellationToken.None);
        return connector.Parse(download.Content);
    }

    [Fact]
    public async Task TwoTermsGiveFourDeputiesWithTheirTerms()
    {
        var (connector, handler, _) = Connector();
        var persons = await CrawlAndParseAsync(connector);

        Assert.Equal(4, persons.Count);
        Assert.Equal(2 + 4, handler.Requests.Count);

        var baska = persons.Single(p => p.SourceKey == "233");
        Assert.Equal(2, baska.Functions.Count);
        Assert.All(baska.Functions, f => Assert.Equal(PepCategory.MemberOfParliament, f.Category));
        Assert.All(baska.Functions, f => Assert.Equal(Country.SK, f.Country));

        var current = baska.Functions.Single(f => f.SourceKey == "term:9");
        Assert.Equal(new DateOnly(2023, 10, 25), current.From);
        Assert.Null(current.To);
        Assert.Equal("Národná rada Slovenskej republiky (9. volebné obdobie)", current.Institution);
        Assert.Equal(NrsrTerms.DateNote, current.DateNote);

        var previous = baska.Functions.Single(f => f.SourceKey == "term:8");
        Assert.Equal(new DateOnly(2020, 3, 20), previous.From);
        Assert.Equal(new DateOnly(2023, 10, 25), previous.To);

        Assert.Single(persons.Single(p => p.SourceKey == "100").Functions, f => f.SourceKey == "term:8");
        Assert.Single(persons.Single(p => p.SourceKey == "1180").Functions, f => f.SourceKey == "term:9");
    }

    [Fact]
    public async Task TheProfileGivesTheIdentity()
    {
        var (connector, _, _) = Connector();
        var persons = await CrawlAndParseAsync(connector);

        var bajo = persons.Single(p => p.SourceKey == "1180");
        Assert.Equal("Martina", bajo.GivenName);
        Assert.Equal("Bajo Holečková", bajo.FamilyName);
        Assert.Equal("Martina Bajo Holečková", bajo.DisplayName);
        Assert.Equal("Mgr.", bajo.TitleBefore);
        Assert.Null(bajo.TitleAfter);
        Assert.Equal(new DateOnly(1984, 2, 6), bajo.BirthDate);
        Assert.Equal("Kozárovce", bajo.Residence);
        Assert.Equal("https://www.nrsr.sk/web/Default.aspx?sid=poslanci/poslanec&PoslanecID=1180&CisObdobia=9", bajo.SourceUrl);

        var osusky = persons.Single(p => p.SourceKey == "100");
        Assert.Equal("MUDr.", osusky.TitleBefore);
        Assert.Equal("CSc.", osusky.TitleAfter);
        Assert.Equal(new DateOnly(1953, 10, 11), osusky.BirthDate);
        Assert.Equal("Bratislava", osusky.Residence);
        Assert.EndsWith("CisObdobia=8", osusky.SourceUrl, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ProfilesAreCachedBetweenRunsAndListsAreNot()
    {
        var (connector, handler, _) = Connector(cacheDays: 30);

        await CrawlAndParseAsync(connector);
        Assert.Equal(6, handler.Requests.Count);

        await CrawlAndParseAsync(connector);
        Assert.Equal(8, handler.Requests.Count);
        Assert.Equal(4, handler.Requests.Skip(6).Count(u => u.Contains("zoznam_abc", StringComparison.Ordinal)) + 2);
    }

    [Fact]
    public async Task TheSameBytesComeOutTwice()
    {
        var (connector, _, _) = Connector(cacheDays: 30);

        using var first = await connector.DownloadAsync(CancellationToken.None);
        using var second = await connector.DownloadAsync(CancellationToken.None);

        using var a = new MemoryStream();
        using var b = new MemoryStream();
        await first.Content.CopyToAsync(a);
        await second.Content.CopyToAsync(b);

        Assert.Equal(a.ToArray(), b.ToArray());
    }

    [Theory]
    [InlineData("6. 2. 1984", 1984, 2, 6)]
    [InlineData("11. 10. 1953", 1953, 10, 11)]
    [InlineData("1.1.2000", 2000, 1, 1)]
    public void ParseDate_ReadsTheSiteFormat(string text, int y, int m, int d)
        => Assert.Equal(new DateOnly(y, m, d), NrsrHtmlParser.ParseDate(text));

    [Theory]
    [InlineData("")]
    [InlineData("neuvedené")]
    [InlineData("31. 2. 1984")]
    public void ParseDate_RefusesWhatIsNotADate(string text)
        => Assert.Null(NrsrHtmlParser.ParseDate(text));

    [Theory]
    [InlineData("3-9", new[] { 3, 4, 5, 6, 7, 8, 9 })]
    [InlineData("9", new[] { 9 })]
    [InlineData("7, 9, 8", new[] { 7, 8, 9 })]
    public void Terms_AreParsed(string text, int[] expected)
        => Assert.Equal(expected, NrsrTerms.Parse(text));

    [Fact]
    public void Terms_RefuseNonsense()
        => Assert.Throws<FormatException>(() => NrsrTerms.Parse("all"));
}
