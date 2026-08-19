using SancMeld.Connectors.FrGels;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Connectors.FrGels.Tests;

/// <summary>
/// Against eight records cut from the published register. The register is
/// entity-attribute-value, so almost everything this parser can get wrong is a lookup
/// that silently finds nothing: a TypeChamp that moved, a subfield renamed, a value that
/// is a number where the last release had a string.
/// </summary>
public class FrGelsJsonParserTests
{
    private const string Fixture = "fixtures/fr-gels/sample.json";

    private static async Task<IReadOnlyList<ParsedDesignation>> ParseAsync()
    {
        await using var file = File.OpenRead(Fixture);
        var parsed = new List<ParsedDesignation>();

        await foreach (var designation in FrGelsJsonParser.ParseAsync(file))
        {
            parsed.Add(designation);
        }

        return parsed;
    }

    private static async Task<ParsedDesignation> SubjectAsync(string id) =>
        (await ParseAsync()).Single(d => d.SourceRecordKey == id);

    [Fact]
    public async Task EveryRecordInTheArrayIsRead()
    {
        var parsed = await ParseAsync();

        Assert.Equal(8, parsed.Count);
        Assert.All(parsed, d => Assert.False(string.IsNullOrWhiteSpace(d.SourceRecordKey)));
        Assert.All(parsed, d => Assert.NotEmpty(d.Names));
    }

    /// <summary>
    /// The family name is the record's own Nom and the given names are a separate
    /// TypeChamp. A parser reading only one of them would index half of every French name.
    /// </summary>
    [Fact]
    public async Task GivenNamesAndFamilyNameBecomeOnePrimaryName()
    {
        var lapin = await SubjectAsync("6699");
        var primary = Assert.Single(lapin.Names, n => n.Kind == NameType.Primary);

        Assert.Equal("Aleksandr Pavlovich LAPIN", primary.Value);
        Assert.Equal("Aleksandr Pavlovich", primary.GivenName);
        Assert.Equal("LAPIN", primary.FamilyName);
        Assert.Contains(lapin.Names, n => n.Value == "Александр Павлович ЛАПИН" && n.Kind == NameType.Alias);
    }

    /// <summary>
    /// This publisher marks its own generic aliases, which no other source here does.
    /// Taking that at its word is better than deriving the flag and hoping.
    /// </summary>
    [Fact]
    public async Task WeakAliasesAreTakenFromThePublisher()
    {
        var subject = await SubjectAsync("2763");

        var weak = Assert.Single(subject.Names, n => n.Value == "Zakir");
        Assert.True(weak.IsWeak);
        Assert.Equal(WeakSource.Source, weak.WeakSource);

        var ordinary = Assert.Single(subject.Names, n => n.Value == "Haji Mullah Sahib");
        Assert.False(ordinary.IsWeak);
    }

    /// <summary>
    /// Day and month are empty on 820 and 805 of the register's 4 347 dates. Reading them
    /// as zero, or defaulting them to the first of January, would turn "born in 1973" into
    /// a date that contradicts every real one.
    /// </summary>
    [Fact]
    public async Task PartialDatesKeepTheirPrecision()
    {
        var full = await SubjectAsync("2457");
        var stated = Assert.Single(full.Births, b => b.Year is not null);
        Assert.Equal((short)1986, stated.Year);
        Assert.Equal((short)11, stated.Month);
        Assert.Equal((short)23, stated.Day);
        Assert.Equal(DatePrecision.Day, stated.Precision);

        var yearOnly = await SubjectAsync("1006");
        var years = yearOnly.Births.Where(b => b.Year is not null).ToList();
        Assert.Equal(2, years.Count);
        Assert.All(years, b => Assert.Equal(DatePrecision.Year, b.Precision));
        Assert.All(years, b => Assert.Null(b.Month));
        Assert.All(years, b => Assert.Null(b.Day));
        Assert.Equal([(short)1973, (short)1983], years.Select(b => b.Year!.Value).Order());
    }

    /// <summary>
    /// A place of birth with no date is still a place of birth, and the register publishes
    /// them as separate entries. Pairing them with the first date would invent a
    /// combination the publisher never stated.
    /// </summary>
    [Fact]
    public async Task PlaceOfBirthIsKeptEvenWithoutADate()
    {
        var subject = await SubjectAsync("1006");
        var place = Assert.Single(subject.Births, b => b.Year is null);

        Assert.Null(place.PlaceOriginal);
        Assert.Equal("KENYA", place.CountryOriginal);
        Assert.Equal("KE", place.CountryIso2);
    }

    /// <summary>
    /// The register writes countries as French names, and a comparison against them is
    /// what tells a screening whether the country supplied agrees.
    /// </summary>
    [Fact]
    public async Task CountriesBecomeIsoCodesWithoutLosingTheWording()
    {
        var nationality = Assert.Single((await SubjectAsync("2457")).Nationalities);

        Assert.Equal("CORÉE DU NORD", nationality.CountryOriginal);
        Assert.Equal("KP", nationality.CountryIso2);
    }

    /// <summary>
    /// Vessels are 39 of the register. Calling them organisations — both being "not a
    /// person" — would make every one of them contradict on subject type against the EU's
    /// own vessel entries.
    /// </summary>
    [Fact]
    public async Task VesselsAreVesselsAndKeepTheirImoNumber()
    {
        var vessel = await SubjectAsync("2218");

        Assert.Equal(SubjectType.Vessel, vessel.SubjectType);
        var imo = Assert.Single(vessel.Identifiers, i => i.Kind == IdentifierType.Imo);
        Assert.Equal("8405270", imo.ValueNormalized);
    }

    /// <summary>
    /// Only the comment says what kind of number an IDENTIFICATION value is, and "/" is
    /// the register's placeholder for "does not apply" — stored, it would be an identifier
    /// that matches every other placeholder in the list.
    /// </summary>
    [Fact]
    public async Task IdentificationKindComesFromTheCommentAndPlaceholdersAreDropped()
    {
        var company = await SubjectAsync("7805");

        Assert.DoesNotContain(company.Identifiers, i => i.ValueOriginal == "/");

        var registration = Assert.Single(company.Identifiers, i => i.ValueNormalized == "1023202741781");
        Assert.Equal(IdentifierType.CompanyRegistration, registration.Kind);

        var tax = Assert.Single((await SubjectAsync("6699")).Identifiers, i => i.ValueNormalized == "344407793386");
        Assert.Equal(IdentifierType.TaxId, tax.Kind);
    }

    /// <summary>
    /// The links this connector was imported for. REFERENCE_UE carries the EU's
    /// euReferenceNumber rather than its logical id, and the published values sometimes
    /// carry a leading space — one that would make the link resolve to nothing.
    /// </summary>
    [Fact]
    public async Task DeclaredLinksToOtherListsAreKeptAndTrimmed()
    {
        var lapin = await SubjectAsync("6699");
        var toEu = Assert.Single(lapin.CrossReferences);
        Assert.Equal("EU_FSF", toEu.TargetSourceCode);
        Assert.Equal("EU.10283.92", toEu.TargetExternalId);

        var withSpace = await SubjectAsync("2457");
        Assert.Equal("EU.4262.62", Assert.Single(withSpace.CrossReferences).TargetExternalId);

        var both = await SubjectAsync("1006");
        Assert.Equal(
            [("EU_FSF", "EU.6192.33"), ("UN_SC", "SOi.019")],
            both.CrossReferences.Select(x => (x.TargetSourceCode, x.TargetExternalId)).Order());
    }

    [Fact]
    public async Task LegalBasisIsAlwaysPresentAndAddressesSurvive()
    {
        var subject = await SubjectAsync("1006");
        Assert.NotEmpty(subject.LegalBasis);
        Assert.All(subject.LegalBasis, l => Assert.Equal("LISTED_BY", l.Relation));

        // Acts are shared per jurisdiction and keyed by type and number, so every citation
        // needs the register's own act id. Without it all 14 691 of them are one act, and a
        // record citing two links to it twice.
        Assert.All(subject.LegalBasis, l => Assert.False(string.IsNullOrWhiteSpace(l.InstrumentNumber)));
        Assert.Equal(
            subject.LegalBasis.Select(l => l.InstrumentNumber).Distinct().Count(),
            subject.LegalBasis.Count);

        var addresses = subject.Addresses.Where(a => a.Street is not null).ToList();
        Assert.Equal(2, addresses.Count);
        Assert.All(addresses, a => Assert.Equal("SO", a.CountryIso2));

        // Phone, e-mail and website are contact details rather than places, and belong to
        // the subject rather than to any one of its addresses.
        var contact = Assert.Single((await SubjectAsync("7805")).Addresses, a => a.ContactInfo is not null);
        Assert.Contains("group@kremny.032.ru", contact.ContactInfo);
        Assert.Contains("https://group-kremny.ru/", contact.ContactInfo);
        Assert.True(contact.IsPartial);
    }

    /// <summary>
    /// The same records read through a stream that hands over a few bytes at a time, so
    /// every record is cut in half by a buffer boundary at least once.
    ///
    /// This is not a hypothetical. The first version of the reader resumed one token too
    /// late after an incomplete record and started parsing at the record's first property
    /// instead of its opening brace, which surfaced as "the target element has type
    /// 'Number'" — on the 12 MB publication, and on nothing smaller. A fixture that fits
    /// in one buffer can never catch it.
    /// </summary>
    [Fact]
    public async Task RecordsSplitAcrossReadsAreStillWhole()
    {
        await using var file = File.OpenRead(Fixture);
        await using var trickle = new TrickleStream(file, bytesPerRead: 7);

        var streamed = new List<ParsedDesignation>();
        await foreach (var designation in FrGelsJsonParser.ParseAsync(trickle))
        {
            streamed.Add(designation);
        }

        var whole = await ParseAsync();

        Assert.Equal(whole.Count, streamed.Count);
        Assert.Equal(
            whole.Select(d => d.SourceRecordKey),
            streamed.Select(d => d.SourceRecordKey));
        Assert.Equal(
            whole.Select(d => d.RawRecordJson),
            streamed.Select(d => d.RawRecordJson));
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

    /// <summary>
    /// The raw fragment is what lets a mapping bug be fixed without re-downloading, and it
    /// has to be the record rather than the file.
    /// </summary>
    [Fact]
    public async Task EveryRecordCarriesItsOwnRawFragment()
    {
        foreach (var designation in await ParseAsync())
        {
            Assert.Contains("\"IdRegistre\"", designation.RawRecordJson);
            Assert.DoesNotContain("\"PublicationDetail\"", designation.RawRecordJson);
        }
    }
}
