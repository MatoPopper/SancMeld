using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SancMeld.Connectors.UnSc;

/// <summary>
/// Turns one INDIVIDUAL or ENTITY into the JSON kept in designation.raw_record.
///
/// The point is to preserve the source verbatim, so a mapping bug can be corrected
/// retroactively without re-downloading, and so fields nobody has modelled yet are not
/// thrown away. raw_record has a GIN index, so the shape matters: attributes become
/// "@name" properties and repeated elements become arrays, which keeps every value
/// reachable by a jsonb query.
/// </summary>
internal static class UnScRawJson
{
    public static string ToJson(XElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(2048);

        using (var writer = new Utf8JsonWriter(buffer))
        {
            WriteObject(writer, element, propertyName: null);
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    private static void WriteObject(Utf8JsonWriter writer, XElement element, string? propertyName)
    {
        if (propertyName is null)
        {
            writer.WriteStartObject();
        }
        else
        {
            writer.WriteStartObject(propertyName);
        }

        foreach (var attribute in element.Attributes())
        {
            writer.WriteString("@" + attribute.Name.LocalName, attribute.Value);
        }

        foreach (var group in element.Elements().GroupBy(child => child.Name.LocalName))
        {
            using var children = group.GetEnumerator();
            children.MoveNext();
            var firstChild = children.Current;

            if (!children.MoveNext())
            {
                WriteChild(writer, firstChild, group.Key);
                continue;
            }

            writer.WriteStartArray(group.Key);
            foreach (var child in group)
            {
                WriteChild(writer, child, propertyName: null);
            }

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    private static void WriteChild(Utf8JsonWriter writer, XElement child, string? propertyName)
    {
        // <remark>text</remark> and <publicationUrl>…</publicationUrl> are values, not
        // structures. Wrapping them in an object would bury the text one level deeper
        // than anyone querying the document would expect.
        if (!child.HasAttributes && !child.HasElements)
        {
            if (propertyName is null)
            {
                writer.WriteStringValue(child.Value);
            }
            else
            {
                writer.WriteString(propertyName, child.Value);
            }

            return;
        }

        WriteObject(writer, child, propertyName);
    }
}
