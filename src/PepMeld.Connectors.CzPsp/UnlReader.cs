using System.Text;

namespace PepMeld.Connectors.CzPsp;

/// <summary>
/// Reads the Informix "unload" format the Chamber publishes: one record per line, fields
/// separated by a pipe, every line ending with one, windows-1250. A pipe or a backslash
/// inside a value is escaped with a backslash.
/// </summary>
internal static class UnlReader
{
    private static readonly Encoding Windows1250 = GetWindows1250();

    public static IEnumerable<string[]> Read(Stream stream)
    {
        using var reader = new StreamReader(stream, Windows1250, detectEncodingFromByteOrderMarks: false, bufferSize: 64 * 1024, leaveOpen: true);

        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                continue;
            }

            yield return Split(line);
        }
    }

    internal static string[] Split(string line)
    {
        var fields = new List<string>(16);
        var current = new StringBuilder();

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (c == '\\' && i + 1 < line.Length && (line[i + 1] == '|' || line[i + 1] == '\\'))
            {
                current.Append(line[++i]);
            }
            else if (c == '|')
            {
                fields.Add(current.ToString());
                current.Clear();
            }
            else
            {
                current.Append(c);
            }
        }

        // The format terminates every record with a pipe, so the text after the last one
        // is empty; a line without the terminator still yields its last field.
        if (current.Length > 0)
        {
            fields.Add(current.ToString());
        }

        return [.. fields];
    }

    private static Encoding GetWindows1250()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1250);
    }
}
