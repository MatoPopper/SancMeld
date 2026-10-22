using System.IO.Compression;
using System.Net;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using PepMeld.Core.Checks;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Lists;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// A mayor without a date of birth gets two possible years from the candidate register,
/// and a check then leaves out a namesake born in another decade while keeping one born
/// in those years as a level C candidate.
/// </summary>
[Collection(DatabaseCollection.Name)]
public sealed class ElectionAgesTests(DatabaseFixture fixture)
{
    private const string Source = "CZ_RPP_OVM";

    private sealed class InlineConnector(IReadOnlyList<ParsedPerson> persons) : IPepListConnector
    {
        public string SourceCode => Source;

        public Task<ListDownload> DownloadAsync(CancellationToken ct) =>
            Task.FromResult(new ListDownload(new MemoryStream(Encoding.UTF8.GetBytes(Guid.NewGuid().ToString())), "inline://", null));

        public IReadOnlyList<ParsedPerson> Parse(Stream content) => persons;
    }

    private sealed class ZipHandler(byte[] zip) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new ByteArrayContent(zip) });
    }

    private sealed class Clients(byte[] zip) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new ZipHandler(zip));
    }

    private static byte[] Register(string town, string given, string surname, int age)
    {
        using var stream = new MemoryStream();
        using (var zip = new ZipArchive(stream, ZipArchiveMode.Create, leaveOpen: true))
        {
            void Write(string name, string text)
            {
                using var writer = new StreamWriter(zip.CreateEntry("csv_od/" + name).Open(), new UTF8Encoding(false));
                writer.Write(text);
            }

            // A council split into two wards has two rows, as in the real register.
            Write("kvrzcoco.csv", $"KRAJ,OKRES,TYPZASTUP,DRUHZASTUP,KODZASTUP,NAZEVZAST,COBVODU\n1,1,1,1,500001,{town},1\n1,1,1,1,500001,{town},2\n");
            Write("kvrk.csv",
                "OKRES,KODZASTUP,COBVODU,POR_STR_HL,OSTRANA,PORCISLO,JMENO,PRIJMENI,TITULPRED,TITULZA,VEK,POVOLANI,BYDLISTEN,PSTRANA,NSTRANA,PLATNOST\n" +
                $"1,500001,0,1,1,1,{given} Josef,{surname},Ing.,,{age},\"starosta, podnikatel\",{town},1,1,A\n" +
                $"1,500002,0,1,1,1,{given},{surname},,,30,student,Jinde,1,1,A\n");
        }

        return stream.ToArray();
    }

    [Fact]
    public async Task AMayorGetsTwoYearsAndACheckLeavesOutANamesakeBornElsewhereInTime()
    {
        var id = Guid.NewGuid().ToString("N")[..8];
        var town = $"Testov{id}";
        var surname = $"Starostik{id}";

        await using (var db = fixture.CreateContext())
        {
            await db.Sources.Where(s => s.Code == Source).ExecuteUpdateAsync(s => s.SetProperty(x => x.MinExpectedPersons, 0));
            var person = new ParsedPerson("v1:" + id, "Petr", surname, null, null, null, null, "inline://" + id,
                [new ParsedFunction("ovm:" + id, Country.CZ, PepCategory.RegionalLeader, $"Město {town}", "starosta obce s rozšířenou působností", null, null, null)]);
            var outcome = await TestServices.Importer(db, new InlineConnector([person]), TestServices.Options(TestServices.TempDirectory()))
                .RunAsync(Source, force: true, CancellationToken.None);
            Assert.Equal(ImportStatus.Success, outcome.Status);
        }

        await using (var db = fixture.CreateContext())
        {
            var ages = new ElectionAges(db, new Clients(Register(town, "Petr", surname, 52)), NullLogger<ElectionAges>.Instance);
            var result = await ages.RunAsync(ElectionAges.Kv2026, force: true, CancellationToken.None);
            Assert.True(result.Set >= 1);
        }

        await using (var db = fixture.CreateContext())
        {
            var mayor = await db.Persons.AsNoTracking().SingleAsync(p => p.SourceKey == "v1:" + id);
            Assert.Equal(1973, mayor.BirthYearMin);
            Assert.Equal(1974, mayor.BirthYearMax);
            Assert.Contains("52", mayor.BirthYearNote, StringComparison.Ordinal);

            var options = TestServices.Options(TestServices.TempDirectory());
            var stage = new ListsStage(new PepListMatcher(db, options), new SourceQueryService(db), options, TimeProvider.System);

            async Task<IReadOnlyList<FindingView>> CheckAsync(DateOnly born) =>
                (await stage.RunAsync(new PepCheck { Name = $"Petr {surname}", BirthDate = born, Country = Country.CZ, UserEmail = "t@example.com" }, CancellationToken.None))
                    .Findings.Where(f => f.Sources?.Contains(Source) == true).ToList();

            var sameYears = Assert.Single(await CheckAsync(new DateOnly(1974, 3, 1)));
            Assert.Equal(EvidenceLevel.C, sameYears.Level);
            Assert.Contains("1973–1974", sameYears.ToVerify, StringComparison.Ordinal);

            Assert.Empty(await CheckAsync(new DateOnly(1995, 3, 1)));
        }
    }
}
