using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// The diff an import makes, run by run, with a connector that hands over exactly the
/// persons the test says. Against the database because the rules that matter here, one
/// row per person and key, a function that ends by vanishing, are enforced by it.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ImportDiffTests(DatabaseFixture fixture)
{
    private const string Source = "SK_WIKIDATA";

    private sealed class InlineConnector(IReadOnlyList<ParsedPerson> persons) : IPepListConnector
    {
        public string SourceCode => Source;

        public Task<ListDownload> DownloadAsync(CancellationToken ct)
        {
            // A different byte for every call, so no run is skipped as unchanged.
            var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString()));
            return Task.FromResult(new ListDownload(stream, "inline://", null));
        }

        public IReadOnlyList<ParsedPerson> Parse(Stream content) => persons;
    }

    private static ParsedFunction Function(string key, DateOnly? to = null) =>
        new(key, Country.SK, PepCategory.GovernmentMember, "Vláda SR", key, new DateOnly(2020, 1, 1), to, null);

    private static ParsedPerson Person(string key, params ParsedFunction[] functions) =>
        new(key, "Test", key, null, null, new DateOnly(1970, 1, 1), null, "inline://" + key, functions);

    private async Task<ImportOutcome> ImportAsync(params ParsedPerson[] persons)
    {
        await using var db = fixture.CreateContext();
        await db.Sources.Where(s => s.Code == Source).ExecuteUpdateAsync(s => s.SetProperty(x => x.MinExpectedPersons, 1));
        return await TestServices.Importer(db, new InlineConnector(persons), TestServices.Options(TestServices.TempDirectory()))
            .RunAsync(Source, force: true, CancellationToken.None);
    }

    [Fact]
    public async Task AKnownPersonGainsAFunctionAndLosesOneByDisappearance()
    {
        var key = "diff-" + Guid.NewGuid().ToString("N")[..8];

        var first = await ImportAsync(Person(key, Function("a")));
        Assert.Equal(ImportStatus.Success, first.Status);
        Assert.Equal(1, first.PersonsInserted);
        Assert.Equal(1, first.FunctionsInserted);

        // The same person, now with a second function: one insert against an unchanged row.
        var second = await ImportAsync(Person(key, Function("a"), Function("b")));
        Assert.Equal(ImportStatus.Success, second.Status);
        Assert.Equal(0, second.PersonsInserted);
        Assert.Equal(1, second.FunctionsInserted);
        Assert.Equal(0, second.FunctionsEnded);

        // The first function is gone from the file: closed with today's date, kept.
        var third = await ImportAsync(Person(key, Function("b")));
        Assert.Equal(ImportStatus.Success, third.Status);
        Assert.Equal(1, third.FunctionsEnded);

        await using var db = fixture.CreateContext();
        var functions = await db.Functions.Where(f => f.Person!.SourceKey == key).OrderBy(f => f.SourceKey).ToListAsync();
        Assert.Equal(2, functions.Count);
        Assert.True(functions[0].EndedByDisappearance);
        Assert.NotNull(functions[0].ValidTo);
        Assert.Null(functions[1].ValidTo);

        // Back in the file: the end is withdrawn, the function is open again.
        var fourth = await ImportAsync(Person(key, Function("a"), Function("b")));
        Assert.Equal(ImportStatus.Success, fourth.Status);
        Assert.Equal(1, fourth.FunctionsUpdated);
        Assert.Null((await db.Functions.AsNoTracking().SingleAsync(f => f.Person!.SourceKey == key && f.SourceKey == "a")).ValidTo);
    }
}
