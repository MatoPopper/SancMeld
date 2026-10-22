using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// A date of birth lent from one list to another's record of the same person: given when
/// name and office agree, kept across re-imports of a source that still has none, withdrawn
/// when the lender goes, and worth level B in a check, never A.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class BirthDateFillTests(DatabaseFixture fixture)
{
    private const string Borrower = "SK_NRSR_VNF";
    private const string Lender = "SK_WIKIDATA";

    private sealed class InlineConnector(string code, IReadOnlyList<ParsedPerson> persons) : IPepListConnector
    {
        public string SourceCode => code;

        public Task<ListDownload> DownloadAsync(CancellationToken ct) =>
            Task.FromResult(new ListDownload(new MemoryStream(System.Text.Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())), "inline://", null));

        public IReadOnlyList<ParsedPerson> Parse(Stream content) => persons;
    }

    private static ParsedPerson Person(string key, string name, DateOnly? born, string role, DateOnly from) =>
        new(key, name.Split(' ')[0], name.Split(' ')[1], null, null, born, null, "inline://" + key,
            [new ParsedFunction("f-" + key, Country.SK, PepCategory.MemberOfParliament, "Národná rada SR", role, from, null, null)]);

    private async Task ImportAsync(string code, params ParsedPerson[] persons)
    {
        await using var db = fixture.CreateContext();
        await db.Sources.Where(s => s.Code == code).ExecuteUpdateAsync(s => s.SetProperty(x => x.MinExpectedPersons, 0));
        var outcome = await TestServices.Importer(db, new InlineConnector(code, persons), TestServices.Options(TestServices.TempDirectory()))
            .RunAsync(code, force: true, CancellationToken.None);
        Assert.Equal(ImportStatus.Success, outcome.Status);
    }

    private async Task<BirthDateFillOutcome> FillAsync()
    {
        await using var db = fixture.CreateContext();
        return await new BirthDateFill(db, NullLogger<BirthDateFill>.Instance).RunAsync(CancellationToken.None);
    }

    private async Task<PepPerson> BorrowerAsync(string key)
    {
        await using var db = fixture.CreateContext();
        return await db.Persons.AsNoTracking().SingleAsync(p => p.SourceKey == key && p.Source!.Code == Borrower);
    }

    [Fact]
    public async Task ADateIsLentForTheSameNameAndOfficeKeptOnReimportAndWithdrawnWhenTheLenderGoes()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var name = $"Lent Borrowerova{id}";
        var born = new DateOnly(1961, 7, 25);
        var seat = new DateOnly(2020, 10, 10);

        // The same name in another office is not the same person: it lends nothing.
        await ImportAsync(Borrower, Person("b-" + id, name, null, "poslanec", seat), Person("o-" + id, $"Other Namesake{id}", null, "poslanec", seat));
        await ImportAsync(Lender, Person("l-" + id, name, born, "poslanec", seat.AddDays(3)), Person("m-" + id, $"Other Namesake{id}", new DateOnly(1978, 4, 9), "predseda vlády", seat));

        var first = await FillAsync();
        Assert.True(first.Filled >= 1);

        var lent = await BorrowerAsync("b-" + id);
        Assert.Equal(born, lent.BirthDate);
        Assert.Equal(Lender, lent.BirthDateFrom);
        Assert.Equal("inline://l-" + id, lent.BirthDateFromUrl);
        Assert.Null((await BorrowerAsync("o-" + id)).BirthDate);

        // A check by name and date finds the borrower at level B, the lender's page cited.
        await using (var db = fixture.CreateContext())
        {
            var options = TestServices.Options(TestServices.TempDirectory());
            var stage = new ListsStage(new PepListMatcher(db, options), new SourceQueryService(db), options, TimeProvider.System);
            var result = await stage.RunAsync(new PepCheck { Name = name, BirthDate = born, Country = Country.SK, UserEmail = "t@example.com" }, CancellationToken.None);

            var finding = Assert.Single(result.Findings, f => f.Sources?.Contains(Borrower) == true);
            Assert.Equal(EvidenceLevel.B, finding.Level);
            Assert.True(finding.Person.DateOfBirthMatched);
            Assert.Contains(finding.Evidence, e => e.Url == "inline://l-" + id);
            Assert.DoesNotContain(finding.Evidence, e => e.Url.StartsWith("inline://b-", StringComparison.Ordinal) && e.Quote!.Contains("nar.", StringComparison.Ordinal));
        }

        // The borrower's own source still has no date: the loan survives its re-import.
        await ImportAsync(Borrower, Person("b-" + id, name, null, "poslanec", seat), Person("o-" + id, $"Other Namesake{id}", null, "poslanec", seat));
        Assert.Equal(born, (await BorrowerAsync("b-" + id)).BirthDate);

        // The lender's record now names someone else: no lender is left, the date is withdrawn.
        await ImportAsync(Lender, Person("l-" + id, $"Renamed Lenderova{id}", born, "poslanec", seat.AddDays(3)));
        var after = await FillAsync();
        Assert.True(after.Withdrawn >= 1);

        var withdrawn = await BorrowerAsync("b-" + id);
        Assert.Null(withdrawn.BirthDate);
        Assert.Null(withdrawn.BirthDateFrom);
    }
}
