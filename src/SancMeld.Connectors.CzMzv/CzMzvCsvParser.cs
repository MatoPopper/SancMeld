using System.Runtime.CompilerServices;
using System.Text;
using SancMeld.Core.Abstractions;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// Reads the published CSV: eleven columns, one header row, and an empty row between
/// every two entries — the file is an export of a document laid out for reading, and
/// the gaps are its paragraph spacing.
///
/// The columns, as the header names them: surname or entity name (every spelling,
/// separated by slashes), given names (likewise), entry status, note on the status,
/// date of birth, nationality or registered seat, date of entry or change, the
/// government resolution, the EU provision whose criteria the subject met, the
/// description of the conduct, and the measures applied.
/// </summary>
internal static class CzMzvCsvParser
{
    private const int Columns = 11;

    public static async IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        using var reader = new StreamReader(source, Encoding.UTF8, detectEncodingFromByteOrderMarks: true, bufferSize: 16 * 1024, leaveOpen: true);

        var number = 0;
        var headerSeen = false;

        await foreach (var record in CsvRecordReader.ReadAsync(reader, ct).ConfigureAwait(false))
        {
            number++;

            if (record.All(string.IsNullOrWhiteSpace))
            {
                continue;
            }

            if (!headerSeen)
            {
                headerSeen = true;

                // The header is the schema. A first column that is not the surname or a
                // column count that moved means the ministry re-cut the table, as it did
                // in July 2026, and every mapping below would be reading the wrong cell.
                if (record.Length < Columns || !record[0].TrimStart('﻿').StartsWith("Příjmení", StringComparison.OrdinalIgnoreCase))
                {
                    throw new CzMzvParseException(
                        $"The header has {record.Length} columns and starts with '{record[0]}'; expected {Columns} starting with 'Příjmení'. " +
                        "The ministry has changed the table.");
                }

                continue;
            }

            if (record.Length < Columns)
            {
                throw new CzMzvParseException(
                    $"Row {number} has {record.Length} columns, fewer than the {Columns} the header declares. The file is damaged.");
            }

            var row = new CzRow(
                number,
                record[0].Trim(),
                record[1].Trim(),
                record[2].Trim(),
                record[3].Trim(),
                record[4].Trim(),
                record[5].Trim(),
                record[6].Trim(),
                record[7].Trim(),
                record[8].Trim(),
                record[9].Trim(),
                record[10].Trim());

            if (CzRowMapper.Map(row) is { } designation)
            {
                yield return designation;
            }
        }

        if (!headerSeen)
        {
            throw new CzMzvParseException("The file has no header row.");
        }
    }
}

/// <summary>One entry of the list, cells as the publisher wrote them.</summary>
internal sealed record CzRow(
    int RowNumber,
    string Surname,
    string GivenNames,
    string Status,
    string StatusNote,
    string BirthDate,
    string Country,
    string ListedAt,
    string Instrument,
    string EuProvision,
    string Reason,
    string Measures);

public sealed class CzMzvParseException(string message) : Exception(message);
