using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Connectors.CzAres.Tests;

public class CzAresStateBoardsConnectorTests
{
    private const string Fixtures = "fixtures/cz-ares-state";

    /// <summary>The day the fixtures were taken, so "ended within ten years" means the same thing next year.</summary>
    private static readonly DateTimeOffset Today = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    private static JsonDocument Record(string ico) => JsonDocument.Parse(File.ReadAllText(Path.Combine(Fixtures, $"vr-{ico}.json")));

    [Fact]
    public void AStateEnterpriseHasItsDirectorAndItsSupervisoryBoard()
    {
        using var record = Record("42196451");
        var seats = AresBoardParser.Seats(record.RootElement);

        Assert.Equal("Lesy České republiky, s.p.", AresBoardParser.Entity(record.RootElement)!.Name);
        Assert.Equal("301", AresBoardParser.Entity(record.RootElement)!.LegalForm);

        Assert.Contains(seats, s => s.Body == "Statutární orgán" && s.Role == "Generální ředitel" && s.BirthDate == new DateOnly(1973, 10, 16));
        Assert.Contains(seats, s => s.Body == "Dozorčí rada" && s.Role == "Předseda dozorčí rady" && s.From == new DateOnly(2023, 1, 11) && s.To is null);
        Assert.All(seats, s => Assert.NotEqual(default, s.BirthDate));
        Assert.All(seats, s => Assert.False(string.IsNullOrWhiteSpace(s.Surname)));

        // The audit committee is prose under "ostatniSkutecnosti", not a body; nothing from it.
        Assert.DoesNotContain(seats, s => s.Body.Contains("audit", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void AJointStockCompanyHasItsBoardOfDirectorsAndNoProcuration()
    {
        using var record = Record("25702556");
        var seats = AresBoardParser.Seats(record.RootElement);

        Assert.Contains(seats, s => s.Role == "předseda představenstva" && s.Surname == "Durčák" && s.GivenName == "Martin" && s.BirthDate == new DateOnly(1966, 11, 25));
        Assert.Contains(seats, s => s.Body == "Dozorčí rada");
        Assert.DoesNotContain(seats, s => s.Body.Contains("Prokura", StringComparison.OrdinalIgnoreCase));
        // Re-registered twice over the years: one person, several rows, the same town.
        Assert.All(seats.Where(s => s.Surname == "Durčák" && s.Role == "předseda představenstva"), s => Assert.Equal("Jesenice", s.Residence));
    }

    [Theory]
    [InlineData("DALIBOR", "Dalibor")]
    [InlineData("ŠAFAŘÍK", "Šafařík")]
    [InlineData("NOVÁKOVÁ-HORÁKOVÁ", "Nováková-Horáková")]
    [InlineData("Martin", "Martin")]
    public void NamesInCapitalsAreWrittenAsNames(string register, string shown)
        => Assert.Equal(shown, AresBoardParser.Title(register));

    private sealed class FixtureHandler : HttpMessageHandler
    {
        public int Requests;

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref Requests);
            var url = request.RequestUri!.ToString();

            string? file = null;
            if (url.EndsWith("/vyhledat", StringComparison.Ordinal))
            {
                file = "search-301-page1.json";
            }
            else if (Regex.Match(url, @"ekonomicke-subjekty-vr/(\d+)") is { Success: true } m)
            {
                file = $"vr-{m.Groups[1].Value}.json";
            }

            var path = file is null ? null : Path.Combine(Fixtures, file);
            if (path is null || !File.Exists(path))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(File.ReadAllText(path), System.Text.Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Factory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }

    private static (CzAresStateBoardsConnector Connector, FixtureHandler Handler) Connector(int historyYears = 10)
    {
        var handler = new FixtureHandler();
        var connector = new CzAresStateBoardsConnector(
            new Factory(handler),
            Options.Create(new CzAresOptions { LegalForms = "301", DelayMilliseconds = 0, HistoryYears = historyYears }),
            new FixedClock(Today),
            NullLogger<CzAresStateBoardsConnector>.Instance);
        return (connector, handler);
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private static async Task<IReadOnlyList<ParsedPerson>> CrawlAndParseAsync(CzAresStateBoardsConnector connector)
    {
        using var download = await connector.DownloadAsync(CancellationToken.None);
        return connector.Parse(download.Content);
    }

    /// <summary>
    /// The fixture search page names six state enterprises, of which only Lesy ČR has a
    /// record in the fixtures; the listed companies add ČEPS. The rest are 404 and
    /// skipped, as a liquidated enterprise that has left the register would be.
    /// </summary>
    [Fact]
    public async Task EnterprisesComeFromTheSearchAndCompaniesFromTheList()
    {
        var (connector, _) = Connector();
        var persons = await CrawlAndParseAsync(connector);

        Assert.NotEmpty(persons);
        Assert.Contains(persons, p => p.Functions.Any(f => f.Institution.StartsWith("Lesy České republiky", StringComparison.Ordinal)));
        Assert.Contains(persons, p => p.Functions.Any(f => f.Institution.StartsWith("ČEPS, a.s.", StringComparison.Ordinal)));
        Assert.All(persons, p => Assert.NotNull(p.BirthDate));
        Assert.All(persons, p => Assert.All(p.Functions, f => Assert.Equal(PepCategory.StateEnterpriseBoard, f.Category)));
        Assert.All(persons, p => Assert.All(p.Functions, f => Assert.Equal(Country.CZ, f.Country)));
    }

    [Fact]
    public async Task TheCompanysReasonForBeingOnTheListIsOnTheSeat()
    {
        var (connector, _) = Connector();
        var persons = await CrawlAndParseAsync(connector);

        var ceps = persons.SelectMany(p => p.Functions).First(f => f.Institution.StartsWith("ČEPS", StringComparison.Ordinal));
        Assert.Contains("IČO 25702556", ceps.Institution);
        Assert.Contains("Ministerstvo průmyslu a obchodu", ceps.Institution);

        var lesy = persons.SelectMany(p => p.Functions).First(f => f.Institution.StartsWith("Lesy", StringComparison.Ordinal));
        Assert.Contains("právní forma 301", lesy.Institution);
    }

    [Fact]
    public async Task SeatsEndedLongAgoAreLeftOut()
    {
        var (recent, _) = Connector(historyYears: 10);
        var (all, _) = Connector(historyYears: 50);

        var recentSeats = (await CrawlAndParseAsync(recent)).Sum(p => p.Functions.Count);
        var allSeats = (await CrawlAndParseAsync(all)).Sum(p => p.Functions.Count);

        Assert.True(allSeats > recentSeats, $"expected the fifty-year window ({allSeats}) to hold more seats than the ten-year one ({recentSeats})");
        Assert.All(await CrawlAndParseAsync(recent), p => Assert.All(p.Functions, f => Assert.True(f.To is null || f.To >= new DateOnly(2016, 10, 20))));
    }

    [Fact]
    public async Task OnePersonOnTwoBoardsIsOnePersonWithTwoSeats()
    {
        var (connector, _) = Connector(historyYears: 50);
        var persons = await CrawlAndParseAsync(connector);

        Assert.Equal(persons.Count, persons.Select(p => p.SourceKey).Distinct().Count());
        Assert.All(persons, p => Assert.StartsWith("v1:", p.SourceKey, StringComparison.Ordinal));
        Assert.All(persons, p => Assert.Equal(p.Functions.Count, p.Functions.Select(f => f.SourceKey).Distinct().Count()));
    }

    [Fact]
    public void TheListOfCompaniesLoadsAndEveryEntryHasAnIcoAndAReason()
    {
        var entities = StateEntities.Load();

        Assert.NotEmpty(entities);
        Assert.All(entities, e => Assert.Matches(@"^\d{8}$", e.Ico));
        Assert.All(entities, e => Assert.False(string.IsNullOrWhiteSpace(e.Why)));
        Assert.Equal(entities.Count, entities.Select(e => e.Ico).Distinct().Count());
    }
}
