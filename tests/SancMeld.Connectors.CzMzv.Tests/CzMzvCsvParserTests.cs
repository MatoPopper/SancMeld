using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Connectors.CzMzv.Tests;

/// <summary>
/// Against fifteen entries cut from the published CSV. The file is an export of a
/// document: empty rows between entries, every spelling of a name in one cell, dates in
/// prose inside the status note. What this parser can get wrong is reading a layout
/// convention as data.
/// </summary>
public class CzMzvCsvParserTests
{
    private const string Fixture = "fixtures/cz-mzv/sample.csv";

    private static async Task<IReadOnlyList<ParsedDesignation>> ParseAsync(Stream? stream = null)
    {
        await using var file = stream ?? File.OpenRead(Fixture);
        var parsed = new List<ParsedDesignation>();

        await foreach (var designation in CzMzvCsvParser.ParseAsync(file))
        {
            parsed.Add(designation);
        }

        return parsed;
    }

    private static async Task<ParsedDesignation> SubjectAsync(string primaryName) =>
        (await ParseAsync()).Single(d => d.Names[0].Value == primaryName);

    /// <summary>
    /// Fifteen entries in the file; three are "změněn", the publisher's own superseded
    /// wording of an entry whose current wording is in a "platný" row, and those are
    /// left out. The empty rows between entries are layout, not records.
    /// </summary>
    [Fact]
    public async Task ValidAndRemovedEntriesAreReadAndSupersededOnesAreNot()
    {
        var parsed = await ParseAsync();

        Assert.Equal(12, parsed.Count);
        Assert.Equal(parsed.Count, parsed.Select(d => d.SourceRecordKey).Distinct().Count());
        Assert.All(parsed, d => Assert.NotEmpty(d.Names));
        Assert.DoesNotContain(parsed, d => d.Remarks is not null && d.Remarks.Contains("byl zápis pozměněn", StringComparison.Ordinal));
    }

    /// <summary>
    /// Seven given-name spellings against three surnames. Each given name takes a
    /// surname of its own script, so "Виктор Владимирович" is paired with МЕДВЕДЧУК
    /// and never with MEDVEDCHUK.
    /// </summary>
    [Fact]
    public async Task EverySpellingIsPairedWithASurnameOfItsOwnScript()
    {
        var medvedchuk = await SubjectAsync("Viktor Volodymyrovyč MEDVEDČUK");

        Assert.Equal(NameType.Primary, medvedchuk.Names[0].Kind);
        Assert.Equal("MEDVEDČUK", medvedchuk.Names[0].FamilyName);
        Assert.Equal("Viktor Volodymyrovyč", medvedchuk.Names[0].GivenName);

        var latin = medvedchuk.Names.Where(n => n.Kind == NameType.Alias).Select(n => n.Value).ToList();
        Assert.Contains("Viktor Volodymyrovič MEDVEDCHUK", latin);
        Assert.Contains("Viktor Vladimirovich MEDVEDCHUK", latin);
        Assert.DoesNotContain(latin, n => n.Contains('М'));

        var native = medvedchuk.Names.Where(n => n.Kind == NameType.NativeScript).Select(n => n.Value).ToList();
        Assert.Equal(["Віктор Володимирович МЕДВЕДЧУК", "Виктор Владимирович МЕДВЕДЧУК"], native);

        // "KHARAZISHVILI/ ხარაზიშვილი" — a stray space after the slash, Georgian script.
        var georgian = await SubjectAsync("Zviad KHARAZISHVILI");
        var script = Assert.Single(georgian.Names, n => n.Kind == NameType.NativeScript);
        Assert.Equal("ზვიად ხარაზიშვილი", script.Value);

        // Two surname spellings beyond the given names: both searchable, under the first
        // given name of their script.
        var gundyayev = await SubjectAsync("Vladimir Michajlovič GUNĎAJEV");
        Assert.Contains(gundyayev.Names, n => n.Value == "Vladimir Mikhailovich GUNDYAYEV");
        Assert.Contains(gundyayev.Names, n => n.Value == "Владимир Михайлович ГУНДЯЕВ" && n.Kind == NameType.NativeScript);
    }

    [Fact]
    public async Task EntitiesHaveNoGivenNameAndKeepEverySpelling()
    {
        var aqah = await SubjectAsync("Al-Qard Al-Hassan Association");

        Assert.Equal(SubjectType.Organisation, aqah.SubjectType);
        Assert.Contains(aqah.Names, n => n.Value == "AQAH" && n.Kind == NameType.Alias);
        Assert.Contains(aqah.Names, n => n.Value == "القرض الحسن" && n.Kind == NameType.NativeScript);

        var seat = Assert.Single(aqah.Addresses);
        Assert.Equal("LB", seat.CountryIso2);
        Assert.Equal("Libanonská republika", seat.CountryOriginal);
        Assert.True(seat.IsPartial);

        var voice = await SubjectAsync("Voice of Europe s.r.o.");
        Assert.Equal("CZ", Assert.Single(voice.Addresses).CountryIso2);
        Assert.Empty(voice.Nationalities);
    }

    /// <summary>
    /// "zrušen" entries stay, dated from the note: "Zápis byl zrušen ke dni 24. února
    /// 2025 … vzhledem k zařazení subjektu na sankční seznam Evropské unie". The reason
    /// — the EU listed them instead — stays readable in Remarks.
    /// </summary>
    [Fact]
    public async Task RemovedEntriesCarryTheDateFromTheNote()
    {
        var yevtushenkov = await SubjectAsync("Vladimír Petrovič JEVTUŠENKO");

        Assert.Equal(DesignationStatus.Delisted, yevtushenkov.Status);
        Assert.Equal(new DateOnly(2025, 2, 24), yevtushenkov.DelistedAt);
        Assert.Equal(new DateOnly(2023, 6, 29), yevtushenkov.ListedAt);
        Assert.Contains("2025/388", yevtushenkov.Remarks);

        var gundyayev = await SubjectAsync("Vladimir Michajlovič GUNĎAJEV");
        Assert.Equal(DesignationStatus.Active, gundyayev.Status);
        Assert.Null(gundyayev.DelistedAt);
    }

    /// <summary>
    /// Zorikov was listed in August 2023, the entry reworded in October 2023 after an
    /// objection. The August row is "změněn" and left out; the October row is the
    /// subject, keyed by its own date.
    /// </summary>
    [Fact]
    public async Task ASubjectListedTwiceIsOneCurrentRecordKeyedByDate()
    {
        var parsed = await ParseAsync();
        var zorikov = parsed.Where(d => d.Names[0].Value == "Rostislav Andrejevič ZORIKOV").ToList();

        var current = Assert.Single(zorikov);
        Assert.Equal("PERSON:rostislav andrejevic zorikov:2023-10-25", current.SourceRecordKey);
        Assert.Equal(DesignationStatus.Active, current.Status);
        Assert.Null(current.ExternalId);
    }

    [Fact]
    public async Task BirthDatesAreExactAndNationalitiesKeepTheirQualifiers()
    {
        var medvedchuk = await SubjectAsync("Viktor Volodymyrovyč MEDVEDČUK");

        var birth = Assert.Single(medvedchuk.Births);
        Assert.Equal(DatePrecision.Day, birth.Precision);
        Assert.Equal(((short)1954, (short)8, (short)7), (birth.Year, birth.Month, birth.Day));

        // "dříve Ukrajina, dnes pravděpodobně Ruská federace"
        Assert.Equal(
            [("UA", "dříve Ukrajina"), ("RU", "dnes pravděpodobně Ruská federace")],
            medvedchuk.Nationalities.Select(n => (n.CountryIso2, n.CountryOriginal)));

        var rogach = await SubjectAsync("Konstantin Valerievych ROGACH");
        Assert.Equal(["RU", "IL"], rogach.Nationalities.Select(n => n.CountryIso2));

        Assert.Empty((await SubjectAsync("Voice of Europe s.r.o.")).Births);
    }

    /// <summary>
    /// Articles, not measures, are what the column names. Article 2 of Regulation
    /// 269/2014 is the freeze; Decision 2020/1999 splits entry (Article 2) from freeze
    /// (Article 3); § 4 odst. 2 of Act 69/2006 covers every area. The programme code
    /// is the EU's own for the same act.
    /// </summary>
    [Fact]
    public async Task MeasuresAndProgrammesComeFromTheArticlesCited()
    {
        var gundyayev = await SubjectAsync("Vladimir Michajlovič GUNĎAJEV");
        Assert.Equal(
            [MeasureType.AssetFreeze, MeasureType.FundsProhibition, MeasureType.EntryBan, MeasureType.TradeRestriction, MeasureType.ServicesRestriction],
            gundyayev.Measures);
        Assert.Equal(["UKR"], gundyayev.Programmes);

        var georgian = await SubjectAsync("Zviad KHARAZISHVILI");
        Assert.Equal([MeasureType.EntryBan, MeasureType.AssetFreeze, MeasureType.FundsProhibition], georgian.Measures);
        Assert.Equal(["HR"], georgian.Programmes);

        var sudliankova = await SubjectAsync("Natallia SUDLIANKOVA");
        Assert.Equal([MeasureType.AssetFreeze, MeasureType.FundsProhibition], sudliankova.Measures);
        Assert.Equal(["RUSDA"], sudliankova.Programmes);

        var aqah = await SubjectAsync("Al-Qard Al-Hassan Association");
        Assert.Equal(["TERR"], aqah.Programmes);
        Assert.Contains(MeasureType.AssetFreeze, aqah.Measures);

        // Cites the Czech act's paragraphs alone, which are not typed: Other, never nothing.
        var voice = await SubjectAsync("Voice of Europe s.r.o.");
        Assert.Equal([MeasureType.Other], voice.Measures);
        Assert.Contains("§ 6 odst. 2", voice.Remarks);
    }

    [Fact]
    public async Task LegalBasisIsTheActAndTheGovernmentResolution()
    {
        var parsed = await ParseAsync();
        Assert.All(parsed, d => Assert.Contains(d.LegalBasis, l => l.InstrumentType == "zákon" && l.InstrumentNumber == "1/2023 Sb."));

        var georgian = parsed.Single(d => d.Names[0].Value == "Zviad KHARAZISHVILI");
        var resolution = Assert.Single(georgian.LegalBasis, l => l.InstrumentType == "usnesení vlády");

        // "Usnesení vlády č. 77 ze dne 29.1.2025" — no spaces in the date this time.
        Assert.Equal("77/2025", resolution.InstrumentNumber);
        Assert.Equal(new DateOnly(2025, 1, 29), resolution.PublicationDate);
        Assert.Equal("LISTED_BY", resolution.Relation);
    }

    [Fact]
    public async Task ReasonIsTheNarrativeAndTheRowTravelsVerbatim()
    {
        var gundyayev = await SubjectAsync("Vladimir Michajlovič GUNĎAJEV");

        Assert.StartsWith("Vladimir Michajlovič Gunďajev, známý rovněž jako patriarcha Kirill", gundyayev.Reason);
        Assert.Contains("\"Surname\":\"GUNĎAJEV/GUNDYAYEV/ГУНДЯЕВ\"", gundyayev.RawRecordJson);
        Assert.Contains("\"Status\":\"platný\"", gundyayev.RawRecordJson);
    }

    /// <summary>
    /// The same file read seven bytes at a time, so every quoted cell — and the
    /// closing quote itself — is split by a read boundary somewhere.
    /// </summary>
    [Fact]
    public async Task RecordsSplitAcrossReadsAreStillWhole()
    {
        await using var file = File.OpenRead(Fixture);
        var trickled = await ParseAsync(new TrickleStream(file, bytesPerRead: 7));
        var whole = await ParseAsync();

        Assert.Equal(whole.Select(d => d.SourceRecordKey), trickled.Select(d => d.SourceRecordKey));
        Assert.Equal(whole.Select(d => d.RawRecordJson), trickled.Select(d => d.RawRecordJson));
    }

    [Fact]
    public async Task AReCutTableIsRefusedRatherThanMisread()
    {
        var csv = "\"Jméno\",\"Příjmení\",\"Stav\"\r\n\"Jan\",\"Novák\",\"platný\"\r\n";
        await using var stream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(csv));

        await Assert.ThrowsAsync<CzMzvParseException>(() => ParseAsync(stream));
    }

    private sealed class TrickleStream(Stream inner, int bytesPerRead) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => throw new NotSupportedException(); }

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            inner.ReadAsync(buffer[..Math.Min(bytesPerRead, buffer.Length)], ct);

        public override int Read(byte[] buffer, int offset, int count) =>
            inner.Read(buffer, offset, Math.Min(bytesPerRead, count));

        public override void Flush() => inner.Flush();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
