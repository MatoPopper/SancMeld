using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Xml;
using SancMeld.Core.Abstractions;

namespace SancMeld.Connectors.NlBz;

/// <summary>
/// Reads the published spreadsheet: one sheet, a title block, a header row, then one
/// entry per row in seven columns — surname, given names, alias, date of birth, place
/// of birth, date of the ministerial decision, Staatscourant reference.
///
/// An ODS is a zip holding content.xml, and the rows are read straight off that XML
/// with a forward-only reader: a cell is a table-cell element whose text is the
/// paragraphs inside it, whose date is an office:date-value attribute when the
/// spreadsheet typed the cell as a date, and which stands for several columns at once
/// when it carries number-columns-repeated — the empty tail of every row does.
/// </summary>
internal static class NlBzOdsParser
{
    private const string TableNs = "urn:oasis:names:tc:opendocument:xmlns:table:1.0";
    private const string OfficeNs = "urn:oasis:names:tc:opendocument:xmlns:office:1.0";
    private const string TextNs = "urn:oasis:names:tc:opendocument:xmlns:text:1.0";
    private const int Columns = 7;

    public static async IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var archive = new ZipArchive(source, ZipArchiveMode.Read, leaveOpen: true);
        var entry = archive.GetEntry("content.xml")
            ?? throw new NlBzParseException("The file has no content.xml; it is a zip but not a spreadsheet.");

        await using var xml = entry.Open();
        using var reader = XmlReader.Create(xml, new XmlReaderSettings { Async = true, IgnoreWhitespace = false });

        var headerSeen = false;
        var number = 0;
        var tables = 0;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != TableNs)
            {
                continue;
            }

            if (reader.LocalName == "table")
            {
                // The list is the first sheet; the other two are empty.
                if (++tables > 1)
                {
                    break;
                }

                continue;
            }

            if (reader.LocalName != "table-row")
            {
                continue;
            }

            number++;
            var cells = await ReadRowAsync(reader).ConfigureAwait(false);

            if (cells.All(c => c.Text.Length == 0))
            {
                continue;
            }

            if (!headerSeen)
            {
                // The title block sits above the header; the header is where the
                // columns are named, and it is the schema. A header that does not start
                // with "Achternaam" means the ministry re-cut the sheet.
                if (!cells[0].Text.StartsWith("Achternaam", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                if (!cells[1].Text.StartsWith("Voornaam", StringComparison.OrdinalIgnoreCase)
                    || !cells[5].Text.StartsWith("Datum", StringComparison.OrdinalIgnoreCase))
                {
                    throw new NlBzParseException(
                        $"The header reads '{string.Join(" | ", cells.Select(c => c.Text))}'; expected surname, given names, " +
                        "alias, date of birth, place of birth, decision date, publication. The ministry has changed the sheet.");
                }

                headerSeen = true;
                continue;
            }

            ct.ThrowIfCancellationRequested();

            yield return NlRowMapper.Map(new NlRow(
                number,
                cells[0].Text,
                cells[1].Text,
                cells[2].Text,
                cells[3].Text,
                cells[3].DateValue,
                cells[4].Text,
                cells[5].Text,
                cells[5].DateValue,
                cells[6].Text));
        }

        if (!headerSeen)
        {
            throw new NlBzParseException("The sheet has no header row starting with 'Achternaam'.");
        }
    }

    /// <summary>The first seven cells of the row the reader is positioned on.</summary>
    private static async Task<NlCell[]> ReadRowAsync(XmlReader reader)
    {
        var cells = new List<NlCell>();

        if (reader.IsEmptyElement)
        {
            return Pad(cells);
        }

        var depth = reader.Depth;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            if (reader.NodeType != XmlNodeType.Element || reader.NamespaceURI != TableNs
                || reader.LocalName is not ("table-cell" or "covered-table-cell"))
            {
                continue;
            }

            var repeated = int.TryParse(reader.GetAttribute("number-columns-repeated", TableNs), out var n) ? n : 1;
            var dateValue = reader.GetAttribute("date-value", OfficeNs);
            var text = reader.IsEmptyElement ? string.Empty : await ReadCellTextAsync(reader).ConfigureAwait(false);

            // A cell repeated 16 000 times is the empty tail of the row. Only the
            // first seven columns are the list.
            for (var i = 0; i < repeated && cells.Count < Columns; i++)
            {
                cells.Add(new NlCell(text, dateValue));
            }
        }

        return Pad(cells);
    }

    /// <summary>
    /// The paragraphs of a cell, joined by a space, with text:s (a run of spaces) and
    /// line breaks honoured. Spans and other inline markup contribute their text.
    /// </summary>
    private static async Task<string> ReadCellTextAsync(XmlReader reader)
    {
        var depth = reader.Depth;
        var text = new StringBuilder();
        var paragraphs = 0;

        while (await reader.ReadAsync().ConfigureAwait(false))
        {
            if (reader.NodeType == XmlNodeType.EndElement && reader.Depth == depth)
            {
                break;
            }

            switch (reader.NodeType)
            {
                case XmlNodeType.Element when reader.NamespaceURI == TextNs && reader.LocalName == "p":
                    if (paragraphs++ > 0)
                    {
                        text.Append(' ');
                    }

                    break;

                case XmlNodeType.Element when reader.NamespaceURI == TextNs && reader.LocalName == "s":
                    text.Append(' ', int.TryParse(reader.GetAttribute("c", TextNs), out var count) ? count : 1);
                    break;

                case XmlNodeType.Element when reader.NamespaceURI == TextNs && reader.LocalName == "line-break":
                    text.Append(' ');
                    break;

                case XmlNodeType.Text:
                case XmlNodeType.SignificantWhitespace:
                case XmlNodeType.CDATA:
                    text.Append(reader.Value);
                    break;
            }
        }

        return string.Join(' ', text.ToString().Split((char[])[' ', '\t', '\r', '\n', ' '], StringSplitOptions.RemoveEmptyEntries));
    }

    private static NlCell[] Pad(List<NlCell> cells)
    {
        while (cells.Count < Columns)
        {
            cells.Add(new NlCell(string.Empty, null));
        }

        return [.. cells];
    }

    private sealed record NlCell(string Text, string? DateValue);
}

/// <summary>One entry of the list, cells as the publisher wrote them.</summary>
internal sealed record NlRow(
    int RowNumber,
    string Surname,
    string GivenNames,
    string Alias,
    string BirthDate,
    string? BirthDateValue,
    string BirthPlace,
    string DecisionDate,
    string? DecisionDateValue,
    string Publication);

public sealed class NlBzParseException(string message) : Exception(message);
