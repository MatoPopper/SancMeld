using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// A list of office holders from an institution's site: a holder whose name is not in the
/// quote is refused, a date of birth comes in as researched, and a holder the next run no
/// longer names has left. A year printed beside the name rules out namesakes.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ResearchListsTests(DatabaseFixture fixture)
{
    private sealed class FixedVerifier(QuoteCheck answer) : IQuoteVerifier
    {
        public Task<QuoteCheck> CheckAsync(string url, string quote, CancellationToken ct) => Task.FromResult(answer);
    }

    private static string Json(string list, params string[] persons) => $$"""
        { "list": "{{list}}", "institution": "Testovací soud", "category": "SeniorJudge", "listUrl": "https://example.org/{{list}}",
          "persons": [ {{string.Join(",", persons)}} ] }
        """;

    private static string Judge(string name, string quote, string? born = null) => born is null
        ? $$"""{ "name": "{{name}}", "role": "soudce", "functionUrl": "https://example.org/s", "functionQuote": "{{quote}}" }"""
        : $$"""{ "name": "{{name}}", "role": "soudce", "functionUrl": "https://example.org/s", "functionQuote": "{{quote}}", "dateOfBirth": "{{born}}", "dobUrl": "https://example.org/d", "dobQuote": "{{name}} (* 12. června 1966 Brno)" }""";

    private async Task<IReadOnlyList<ResearchListOutcome>> AddAsync(string snapshots, string json)
    {
        await using var db = fixture.CreateContext();
        var options = TestServices.Options(snapshots);
        var importer = new PepImportService(db, [new ResearchListConnector(options)], new FileSystemSnapshotStore(options), TimeProvider.System, NullLogger<PepImportService>.Instance);
        var lists = new ResearchLists(db, new FixedVerifier(QuoteCheck.Exact), importer, options, TimeProvider.System, NullLogger<ResearchLists>.Instance);
        return await lists.AddAsync(new MemoryStream(Encoding.UTF8.GetBytes(json)), CancellationToken.None);
    }

    [Fact]
    public async Task HoldersAreCheckedImportedAndEndedWhenTheNextListNoLongerNamesThem()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var list = "test-" + id;
        var snapshots = TestServices.TempDirectory();
        var a = $"Alena Soudkova{id}";
        var b = $"Bedrich Soudce{id}";

        var first = await AddAsync(snapshots, Json(list,
            Judge(a, $"JUDr. {a}", "1966-06-12"),
            Judge(b, $"JUDr. {b}"),
            Judge($"Cyril Chybny{id}", "JUDr. Někdo Jiný")));

        Assert.True(Assert.Single(first, o => o.Name == a).DateTaken);
        Assert.False(Assert.Single(first, o => o.Name.StartsWith("Cyril", StringComparison.Ordinal)).Accepted);

        await using (var db = fixture.CreateContext())
        {
            var alena = await db.Persons.AsNoTracking().Include(p => p.Functions).SingleAsync(p => p.SourceKey == ResearchListConnector.PersonKey(list, a));
            Assert.Equal(new DateOnly(1966, 6, 12), alena.BirthDate);
            Assert.Equal(BirthDateResearch.Researched, alena.BirthDateFrom);
            Assert.Equal(PepCategory.SeniorJudge, Assert.Single(alena.Functions).Category);
            Assert.False(await db.Persons.AnyAsync(p => p.SourceKey == ResearchListConnector.PersonKey(list, $"Cyril Chybny{id}")));
        }

        // The next run names only Bedřich: Alena has left the court.
        await AddAsync(snapshots, Json(list, Judge(b, $"JUDr. {b}")));

        await using (var db = fixture.CreateContext())
        {
            var gone = await db.Functions.AsNoTracking().SingleAsync(f => f.Person!.SourceKey == ResearchListConnector.PersonKey(list, a));
            Assert.True(gone.EndedByDisappearance);
            Assert.NotNull(gone.ValidTo);
        }
    }

    private static string YearJudge(string name, string yearQuote) =>
        $$"""{ "name": "{{name}}", "role": "soudce", "functionUrl": "https://example.org/s", "functionQuote": "JUDr. {{name}}", "birthYear": 1974, "birthYearUrl": "https://example.org/y", "birthYearQuote": "{{yearQuote}}" }""";

    [Fact]
    public async Task AYearBesideTheNameIsKeptAndRulesOutANamesakeBornInAnotherYear()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var list = "test-" + id;
        var snapshots = TestServices.TempDirectory();
        var named = $"Dana Rocnikova{id}";
        var bare = $"Emil Rocnik{id}";

        var outcomes = await AddAsync(snapshots, Json(list,
            YearJudge(named, $"Mgr. {named} *1974"),
            YearJudge(bare, "*1974 Základní informace")));

        Assert.True(Assert.Single(outcomes, o => o.Name == named).YearTaken);
        Assert.False(Assert.Single(outcomes, o => o.Name == bare).YearTaken);

        await using var db = fixture.CreateContext();
        var dana = await db.Persons.AsNoTracking().SingleAsync(p => p.SourceKey == ResearchListConnector.PersonKey(list, named));
        Assert.Equal(1974, dana.BirthYearMin);
        Assert.Equal(1974, dana.BirthYearMax);
        Assert.Null(dana.BirthDate);
        Assert.Null((await db.Persons.AsNoTracking().SingleAsync(p => p.SourceKey == ResearchListConnector.PersonKey(list, bare))).BirthYearMin);

        var options = TestServices.Options(TestServices.TempDirectory());
        var stage = new ListsStage(new PepListMatcher(db, options), new SourceQueryService(db), options, TimeProvider.System);

        async Task<IReadOnlyList<FindingView>> CheckAsync(DateOnly born) =>
            (await stage.RunAsync(new PepCheck { Name = named, BirthDate = born, Country = Country.CZ, UserEmail = "t@example.com" }, CancellationToken.None))
                .Findings.Where(f => f.Sources?.Contains(ResearchLists.SourceCode) == true).ToList();

        var sameYear = Assert.Single(await CheckAsync(new DateOnly(1974, 5, 1)));
        Assert.Equal(EvidenceLevel.C, sameYear.Level);
        Assert.Contains("rok narodenia 1974", sameYear.ToVerify, StringComparison.Ordinal);
        Assert.Empty(await CheckAsync(new DateOnly(1975, 5, 1)));
    }
}
