using System.Globalization;
using System.Text.Json;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// Turns one row of the workbook into a listing.
///
/// The publisher assigns no identifier. The record key is therefore derived — sheet,
/// normalised primary name, date listed — and documented here as FingerprintVersion 1.
/// The date is part of it because a name alone is not unique over time: a subject
/// delisted and listed again is a second decision, and the list keeps both rows. The
/// cost is that a corrected spelling of a name becomes a new record and leaves the old
/// one PossiblyMissing; that is visible in the run, which is the right place for it.
/// </summary>
internal static class PlRowMapper
{
    private static readonly JsonSerializerOptions RawOptions = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>
    /// The act every listing rests on. The ministry's decisions cite it; the workbook
    /// does not name it at all, so it is stated once here rather than inferred per row.
    /// Dates from the Sejm's ELI record (api.sejm.gov.pl/eli/acts/DU/2022/835).
    /// </summary>
    private static readonly ParsedLegalInstrument Act = new()
    {
        Relation = "LISTED_BY",
        InstrumentType = "ustawa",
        InstrumentNumber = "Dz.U. 2022 poz. 835",
        Title = "Ustawa z dnia 13 kwietnia 2022 r. o szczególnych rozwiązaniach w zakresie przeciwdziałania " +
                "wspieraniu agresji na Ukrainę oraz służących ochronie bezpieczeństwa narodowego",
        EffectiveDate = new DateOnly(2022, 4, 16),
        OfficialUrl = "https://isap.sejm.gov.pl/isap.nsf/DocDetails.xsp?id=WDU20220000835",
    };

    public static ParsedDesignation Map(PlRow row)
    {
        ArgumentNullException.ThrowIfNull(row);

        var isPerson = row.Sheet == PlSheet.Persons;
        var names = PlNames.Parse(row.Sheet, row.Name);

        if (names.Count == 0)
        {
            throw new PlMswiaParseException($"Row {row.RowNumber} has a name cell that yields no name: '{row.Name}'.");
        }

        var listedAt = Date(row.ListedAt);
        var delistedAt = Date(row.DelistedAt);
        var (measures, programmes) = PlMeasures.Parse(row.Measures);

        var facts = isPerson ? PlPersonIdentity.Parse(row.Identity) : null;
        var entity = isPerson ? null : PlEntityIdentity.Parse(row.Identity);

        return new ParsedDesignation
        {
            ExternalId = null,
            SourceRecordKey = Key(row.Sheet, names[0].Value, listedAt),
            FingerprintVersion = 1,
            SubjectType = isPerson ? SubjectType.Person : SubjectType.Organisation,

            // The list keeps delisted rows, dated. They stay here as what they are rather
            // than vanishing: a decision taken while the subject was listed still needs
            // the listing it was taken against.
            Status = delistedAt is null ? DesignationStatus.Active : DesignationStatus.Delisted,
            ListedAt = listedAt,
            DelistedAt = delistedAt,
            Reason = Blank(row.Reason),

            // The measures column verbatim: the enum says a freeze applies, the text says
            // which regulation defines the assets and whether a court has since set the
            // decision aside.
            Remarks = Blank(row.Measures),
            Names = names,
            Births = facts?.Births ?? [],
            Nationalities = facts?.Nationalities ?? [],
            Identifiers = facts?.Identifiers ?? entity!.Identifiers,
            Addresses = entity?.Addresses ?? [],
            Measures = measures,
            Programmes = programmes,
            LegalBasis = LegalBasis(row.DecisionLink),
            RawRecordJson = Raw(row),
        };
    }

    private static string Key(PlSheet sheet, string primaryName, DateOnly? listedAt) =>
        string.Create(CultureInfo.InvariantCulture,
            $"{(sheet == PlSheet.Persons ? "PERSON" : "ENTITY")}:{NameNormalizer.Normalize(primaryName).Trim()}:{listedAt?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "-"}");

    /// <summary>
    /// The decision is linked as a gov.pl attachment on most rows. Not on all: some rows
    /// have no link, and one links a file on the editor's own disk (file:///C:/Users/…,
    /// seen 2026-10-15). Only a public URL is a citation anyone else can follow.
    /// </summary>
    private static List<ParsedLegalInstrument> LegalBasis(Uri? decision)
    {
        var basis = new List<ParsedLegalInstrument> { Act };

        if (decision is { IsAbsoluteUri: true } && (decision.Scheme == Uri.UriSchemeHttps || decision.Scheme == Uri.UriSchemeHttp))
        {
            basis.Add(new ParsedLegalInstrument
            {
                Relation = "LISTED_BY",
                InstrumentType = "decyzja Ministra Spraw Wewnętrznych i Administracji",

                // The attachment id is the only number the decision has in the file. The
                // decision's own reference sits inside the PDF.
                InstrumentNumber = decision.Segments[^1].Trim('/'),
                OfficialUrl = decision.AbsoluteUri,
            });
        }

        return basis;
    }

    /// <summary>
    /// Dates are Excel serials in the published cells (44861 = 2022-10-27) — a text date
    /// is accepted too, since a hand-edited cell is how this file is maintained.
    /// </summary>
    private static DateOnly? Date(string cell)
    {
        var text = cell.Trim();

        if (text.Length == 0)
        {
            return null;
        }

        if (double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var serial) && serial is > 0 and < 2_958_466)
        {
            return DateOnly.FromDateTime(DateTime.FromOADate(serial));
        }

        return DateOnly.TryParseExact(text.TrimEnd('r', '.', ' '), ["d.M.yyyy", "yyyy-MM-dd"], CultureInfo.InvariantCulture, DateTimeStyles.None, out var date)
            ? date
            : null;
    }

    private static string? Blank(string text) =>
        string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static string Raw(PlRow row) =>
        JsonSerializer.Serialize(new
        {
            sheet = row.Sheet == PlSheet.Persons ? PlMswiaXlsxParser.PersonsSheet : PlMswiaXlsxParser.EntitiesSheet,
            row = row.RowNumber,
            name = row.Name,
            identity = row.Identity,
            reason = row.Reason,
            measures = row.Measures,
            listedAt = row.ListedAt,
            delistedAt = row.DelistedAt,
            decision = row.DecisionLink?.OriginalString,
        }, RawOptions);
}
