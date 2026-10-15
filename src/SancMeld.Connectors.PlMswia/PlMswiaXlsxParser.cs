using System.Globalization;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using DocumentFormat.OpenXml;
using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Spreadsheet;
using SancMeld.Core.Abstractions;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// Reads the published workbook: two sheets, "osoby" (persons) and "podmioty" (entities),
/// six columns each — name, identifying data, justification, measures, date listed, date
/// delisted — and a hyperlink on the name to the ministerial decision.
///
/// It is a document, not a dataset. Every cell is prose written for a reader: a date of
/// birth is "urodzony 5 października 1973 r. w Wilnie (Litwa)", a company number is "Nr
/// KRS 0000312056" on one row and "KRS: 0000312056" on the next, and the footnotes sit in
/// the name column after the last subject. This class only gets the cells out; what they
/// mean is <see cref="PlRowMapper"/>'s problem.
///
/// Rows are streamed through OpenXmlReader rather than loaded as a worksheet. The file
/// is 200 kB today, which would survive either, but the connector contract is the same
/// for every source and this one has no reason to be the exception.
/// </summary>
internal static partial class PlMswiaXlsxParser
{
    public const string PersonsSheet = "osoby";
    public const string EntitiesSheet = "podmioty";

    public static async IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        // The package reader is synchronous. Yielding once keeps the enumeration off the
        // caller's thread the way the other connectors' readers are.
        await Task.Yield();

        using var document = SpreadsheetDocument.Open(source, isEditable: false);
        var workbook = document.WorkbookPart
            ?? throw new PlMswiaParseException("The file has no workbook part; it is not a spreadsheet.");

        var shared = SharedStrings(workbook);
        var seen = new HashSet<PlSheet>();

        var sheets = workbook.Workbook?.Sheets?.Elements<Sheet>()
            ?? throw new PlMswiaParseException("The workbook lists no sheets.");

        foreach (var sheet in sheets)
        {
            var name = sheet.Name?.Value ?? string.Empty;

            // A sheet this connector does not know is a category of subject it does not
            // import — vessels, say. Importing the two it knows and saying nothing would
            // be exactly the silent partial result a sanctions connector must not give.
            var kind = name switch
            {
                PersonsSheet => PlSheet.Persons,
                EntitiesSheet => PlSheet.Entities,
                _ => throw new PlMswiaParseException(
                    $"The workbook has a sheet named '{name}'. Only '{PersonsSheet}' and '{EntitiesSheet}' are known; " +
                    "the ministry has changed how the list is published."),
            };

            seen.Add(kind);
            var part = (WorksheetPart)workbook.GetPartById(sheet.Id!.Value!);
            var links = Hyperlinks(part);

            foreach (var row in Rows(part, shared, links, kind))
            {
                ct.ThrowIfCancellationRequested();
                yield return PlRowMapper.Map(row);
            }
        }

        if (seen.Count < 2)
        {
            throw new PlMswiaParseException(
                $"The workbook lacks the '{PersonsSheet}' or the '{EntitiesSheet}' sheet. " +
                "Half a list must not be published as the whole of it.");
        }
    }

    private static IEnumerable<PlRow> Rows(
        WorksheetPart part,
        IReadOnlyList<string> shared,
        IReadOnlyDictionary<string, Uri> links,
        PlSheet kind)
    {
        using var reader = OpenXmlReader.Create(part);

        while (reader.Read())
        {
            if (reader.ElementType != typeof(Row) || !reader.IsStartElement)
            {
                continue;
            }

            var row = (Row)reader.LoadCurrentElement()!;
            var number = row.RowIndex?.Value ?? 0;
            var cells = new Dictionary<string, string>(StringComparer.Ordinal);

            foreach (var cell in row.Elements<Cell>())
            {
                if (cell.CellReference?.Value is not { Length: > 0 } reference)
                {
                    continue;
                }

                cells[Column(reference)] = Text(cell, shared);
            }

            var name = cells.GetValueOrDefault("A", string.Empty).Trim();

            if (number == 1 || cells.Values.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            // The footnotes the measures cite ("[1] W rozumieniu rozporządzenia Rady (UE)
            // nr 269/2014…") are rows in the same table, after the last subject. They
            // are text about the list, not entries on it.
            if (FootnotePattern().IsMatch(name))
            {
                continue;
            }

            if (name.Length == 0)
            {
                throw new PlMswiaParseException(
                    $"Row {number} of '{SheetName(kind)}' has identifying data but no name. A subject without a " +
                    "name cannot be screened, and dropping the row would hide that the file is damaged.");
            }

            yield return new PlRow(
                kind,
                number,
                name,
                cells.GetValueOrDefault("B", string.Empty),
                cells.GetValueOrDefault("C", string.Empty),
                cells.GetValueOrDefault("D", string.Empty),
                cells.GetValueOrDefault("E", string.Empty),
                cells.GetValueOrDefault("F", string.Empty),
                links.GetValueOrDefault("A" + number.ToString(CultureInfo.InvariantCulture)));
        }
    }

    /// <summary>
    /// The link on each name is the ministerial decision that listed the subject — the
    /// closest thing to a legal citation the file carries. It lives after the rows in
    /// the sheet XML, so it is read in a pass of its own before the rows are.
    /// </summary>
    private static Dictionary<string, Uri> Hyperlinks(WorksheetPart part)
    {
        var links = new Dictionary<string, Uri>(StringComparer.Ordinal);
        var relationships = part.HyperlinkRelationships.ToDictionary(r => r.Id, r => r.Uri, StringComparer.Ordinal);

        using var reader = OpenXmlReader.Create(part);

        while (reader.Read())
        {
            if (reader.ElementType != typeof(Hyperlinks) || !reader.IsStartElement)
            {
                continue;
            }

            foreach (var link in ((Hyperlinks)reader.LoadCurrentElement()!).Elements<Hyperlink>())
            {
                if (link.Reference?.Value is { Length: > 0 } reference
                    && link.Id?.Value is { Length: > 0 } id
                    && relationships.TryGetValue(id, out var uri))
                {
                    links[reference] = uri;
                }
            }

            break;
        }

        return links;
    }

    private static List<string> SharedStrings(WorkbookPart workbook)
    {
        var table = workbook.SharedStringTablePart?.SharedStringTable;
        return table is null ? [] : [.. table.Elements<SharedStringItem>().Select(item => item.InnerText)];
    }

    private static string Text(Cell cell, IReadOnlyList<string> shared)
    {
        var dataType = cell.DataType?.Value;

        if (dataType == CellValues.SharedString)
        {
            return int.TryParse(cell.CellValue?.Text, NumberStyles.None, CultureInfo.InvariantCulture, out var index)
                && index >= 0 && index < shared.Count
                    ? shared[index]
                    : string.Empty;
        }

        if (dataType == CellValues.InlineString)
        {
            return cell.InlineString?.InnerText ?? string.Empty;
        }

        return cell.CellValue?.Text ?? string.Empty;
    }

    private static string Column(string reference) =>
        new([.. reference.TakeWhile(char.IsLetter)]);

    private static string SheetName(PlSheet kind) => kind == PlSheet.Persons ? PersonsSheet : EntitiesSheet;

    [GeneratedRegex(@"^\[\d+\]")]
    private static partial Regex FootnotePattern();
}

internal enum PlSheet { Persons, Entities }

/// <summary>One row of either sheet, cells as the publisher wrote them.</summary>
internal sealed record PlRow(
    PlSheet Sheet,
    uint RowNumber,
    string Name,
    string Identity,
    string Reason,
    string Measures,
    string ListedAt,
    string DelistedAt,
    Uri? DecisionLink);

public sealed class PlMswiaParseException(string message) : Exception(message);
