using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Connectors.SkNrsrVnf.Tests;

public class SkNrsrVnfConnectorTests
{
    private const string Fixtures = "fixtures/sk-nrsr-vnf";

    /// <summary>Serves the saved pages: a GET of the list is the bare page, a POST to it the alphabetical view, a declaration by its UserId.</summary>
    private sealed class FixtureHandler : HttpMessageHandler
    {
        private readonly Lock gate = new();

        public List<(string Method, string Url, string? Body)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString();
            var body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            // Declarations arrive on several lanes at once.
            lock (gate)
            {
                Requests.Add((request.Method.Method, url, body));
            }

            string? file = null;
            if (url.Contains("sid=vnf/zoznam", StringComparison.Ordinal))
            {
                file = request.Method == HttpMethod.Post ? "zoznam-abc.html" : "zoznam.html";
            }
            else if (Regex.Match(url, @"UserId=([^&]+)") is { Success: true } m)
            {
                file = $"oznamenie-{Uri.UnescapeDataString(m.Groups[1].Value)}.html";
            }

            var path = file is null ? null : Path.Combine(Fixtures, file);
            if (path is null || !File.Exists(path))
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            return new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(path), System.Text.Encoding.UTF8, "text/html") };
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (SkNrsrVnfConnector Connector, FixtureHandler Handler) Connector(int cacheDays = 0)
    {
        var handler = new FixtureHandler();
        var snapshots = Path.Combine(Path.GetTempPath(), "pepmeld-vnf-tests", Guid.NewGuid().ToString("N"));
        var connector = new SkNrsrVnfConnector(
            new Factory(handler),
            Options.Create(new SkNrsrVnfOptions { DelayMilliseconds = 0, DeclarationCacheDays = cacheDays }),
            Options.Create(new PepMeldOptions { Ingest = new IngestOptions { SnapshotStoragePath = snapshots } }),
            TimeProvider.System,
            NullLogger<SkNrsrVnfConnector>.Instance);
        return (connector, handler);
    }

    private static async Task<IReadOnlyList<ParsedPerson>> CrawlAndParseAsync(SkNrsrVnfConnector connector)
    {
        using var download = await connector.DownloadAsync(CancellationToken.None);
        return connector.Parse(download.Content);
    }

    [Fact]
    public async Task TheListIsPostedWithTheFormsHiddenFieldsAndEveryDeclarationIsRead()
    {
        var (connector, handler) = Connector();
        var persons = await CrawlAndParseAsync(connector);

        Assert.Equal(3, persons.Count);
        Assert.Equal(5, handler.Requests.Count);

        var post = handler.Requests.Single(r => r.Method == "POST");
        Assert.Contains("__VIEWSTATE=", post.Body);
        Assert.Contains("__EVENTVALIDATION=", post.Body);
        Assert.Contains("ViewsList=1", post.Body);
        Assert.Contains("ShowSelectedView=Zobrazi", post.Body);
    }

    [Fact]
    public async Task ABoardMemberHasOneFunctionPerCompanyWithTheCompanyAsInstitution()
    {
        var (connector, _) = Connector();
        var balog = (await CrawlAndParseAsync(connector)).Single(p => p.SourceKey == "BALOBORI");

        Assert.Equal("Boris", balog.GivenName);
        Assert.Equal("Balog", balog.FamilyName);
        Assert.Equal("doc. JUDr.", balog.TitleBefore);
        Assert.Equal("PhD.", balog.TitleAfter);
        Assert.Null(balog.BirthDate);
        Assert.Equal("https://www.nrsr.sk/web/Default.aspx?sid=vnf/oznamenie&UserId=BALOBORI", balog.SourceUrl);

        Assert.Equal(10, balog.Functions.Count);
        Assert.All(balog.Functions, f => Assert.Equal(PepCategory.StateEnterpriseBoard, f.Category));
        Assert.All(balog.Functions, f => Assert.Equal(Country.SK, f.Country));
        Assert.All(balog.Functions, f => Assert.Null(f.From));
        Assert.All(balog.Functions, f => Assert.Contains("oznámenia za rok 2024", f.DateNote));
        Assert.Contains(balog.Functions, f => f.Institution == "NAFTA, A.S." && f.Role == "člen kontrolného orgánu alebo dozorného orgánu právnickej osoby");
        Assert.Contains(balog.Functions, f => f.Institution == "RUDNÉ BANE, Š.P.");
        Assert.Equal(10, balog.Functions.Select(f => f.SourceKey).Distinct().Count());
    }

    [Fact]
    public async Task ThePresidentAndAMayorAreClassified()
    {
        var (connector, _) = Connector();
        var persons = await CrawlAndParseAsync(connector);

        var president = persons.Single(p => p.SourceKey == "PelePete");
        Assert.Equal("Peter Pellegrini", president.DisplayName);
        Assert.Equal("Ing.", president.TitleBefore);
        Assert.Contains(president.Functions, f => f.Category == PepCategory.HeadOfState && f.Institution == "Slovenská republika");
        Assert.Contains(president.Functions, f => f.Category == PepCategory.MemberOfParliament);

        var mayor = persons.Single(p => p.SourceKey == "Tomas.Abel");
        var office = Assert.Single(mayor.Functions);
        Assert.Equal(PepCategory.RegionalLeader, office.Category);
        Assert.Equal("primátor mesta", office.Role);
        Assert.Equal("JUDr.", mayor.TitleBefore);
        Assert.Equal("PhD.", mayor.TitleAfter);
    }

    [Fact]
    public async Task DeclarationsAreCachedAndTheListIsNot()
    {
        var (connector, handler) = Connector(cacheDays: 30);

        await CrawlAndParseAsync(connector);
        Assert.Equal(5, handler.Requests.Count);

        await CrawlAndParseAsync(connector);
        Assert.Equal(7, handler.Requests.Count);
    }

    [Theory]
    [InlineData("člen kontrolného orgánu alebo dozorného orgánu právnickej osoby (NAFTA, A.S.)", PepCategory.StateEnterpriseBoard, "NAFTA, A.S.")]
    [InlineData("riaditeľ štátneho podniku (LESY SR, Š.P. )", PepCategory.StateEnterpriseBoard, "LESY SR, Š.P.")]
    [InlineData("člen Bankovej rady Národnej banky Slovenska", PepCategory.CentralBankBoard, "Národná banka Slovenska")]
    [InlineData("sudca Ústavného súdu Slovenskej republiky", PepCategory.SeniorJudge, "Ústavný súd Slovenskej republiky")]
    [InlineData("štátny tajomník", PepCategory.GovernmentMember, "Vláda Slovenskej republiky")]
    [InlineData("predseda vyššieho územného celku", PepCategory.RegionalLeader, "vyšší územný celok")]
    [InlineData("generálny prokurátor Slovenskej republiky", PepCategory.Prosecutor, "prokuratúra")]
    [InlineData("verejný ochranca práv", PepCategory.OtherProminentFunction, "verejný ochranca práv")]
    public void Classify_MapsTheWordingToACategory(string declared, PepCategory category, string institution)
    {
        var function = VnfFunctions.Classify(declared);

        Assert.Equal(category, function.Category);
        Assert.Equal(institution, function.Institution);
    }

    [Theory]
    [InlineData("doc. JUDr. BORIS BALOG, PhD.", "doc. JUDr.", "PhD.")]
    [InlineData("Ing. PETER PELLEGRINI", "Ing.", null)]
    [InlineData("MARTINA BAJO HOLEČKOVÁ", null, null)]
    [InlineData("prof. MUDr. JÁN NOVÁK, DrSc., MPH", "prof. MUDr.", "DrSc., MPH")]
    public void Titles_AreSplitAroundTheUpperCaseName(string header, string? before, string? after)
        => Assert.Equal((before, after), VnfHtmlParser.Titles(header));
}
