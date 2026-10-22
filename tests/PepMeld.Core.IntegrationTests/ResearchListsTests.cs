using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Family;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// A list of office holders from an institution's site: a holder whose name is not in the
/// quote is refused, a date of birth comes in as researched, and a holder the next run no
/// longer names has left.
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
}
