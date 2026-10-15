using System.Runtime.CompilerServices;
using System.Text;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// Reads RFC 4180 records off a stream of characters: quoted fields, doubled quotes,
/// line breaks inside quotes, CRLF or LF between records.
///
/// Written here rather than taken from a package because the whole of what the file
/// needs is this one method, and the list's cells — multi-paragraph justifications
/// with quotation marks in them — are exactly the cases a split-on-comma would get
/// wrong. Records come out as they are completed, so a record cut by a read boundary
/// is never seen in halves.
/// </summary>
internal static class CsvRecordReader
{
    public static async IAsyncEnumerable<string[]> ReadAsync(
        TextReader reader,
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        var buffer = new char[16 * 1024];
        var field = new StringBuilder();
        var record = new List<string>();
        var inQuotes = false;
        var fieldWasQuoted = false;
        var afterCr = false;

        // A quote that was the last character of a read: a doubled quote or the end of
        // the field, which only the next character can say.
        var pendingQuote = false;

        while (true)
        {
            var read = await reader.ReadAsync(buffer.AsMemory(), ct).ConfigureAwait(false);

            if (read == 0)
            {
                break;
            }

            for (var i = 0; i < read; i++)
            {
                var c = buffer[i];

                if (inQuotes)
                {
                    if (c != '"')
                    {
                        field.Append(c);
                    }
                    else if (i + 1 < read && buffer[i + 1] == '"')
                    {
                        field.Append('"');
                        i++;
                    }
                    else
                    {
                        inQuotes = false;
                        fieldWasQuoted = true;
                        pendingQuote = i + 1 == read;
                    }

                    continue;
                }

                if (pendingQuote)
                {
                    pendingQuote = false;
                    if (c == '"')
                    {
                        field.Append('"');
                        inQuotes = true;
                        continue;
                    }
                }

                if (afterCr && c != '\n')
                {
                    // A bare CR ended the record; CRLF is finished by the LF below.
                    record.Add(field.ToString());
                    field.Clear();
                    fieldWasQuoted = false;
                    yield return [.. record];
                    record.Clear();
                }

                afterCr = false;

                switch (c)
                {
                    case '"' when field.Length == 0 && !fieldWasQuoted:
                        inQuotes = true;
                        break;

                    case ',':
                        record.Add(field.ToString());
                        field.Clear();
                        fieldWasQuoted = false;
                        break;

                    case '\r':
                        afterCr = true;
                        break;

                    case '\n':
                        record.Add(field.ToString());
                        field.Clear();
                        fieldWasQuoted = false;
                        yield return [.. record];
                        record.Clear();
                        break;

                    default:
                        field.Append(c);
                        break;
                }
            }
        }

        if (field.Length > 0 || record.Count > 0 || fieldWasQuoted || afterCr)
        {
            record.Add(field.ToString());
            yield return [.. record];
        }
    }
}
