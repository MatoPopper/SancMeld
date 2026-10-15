using System.Buffers;
using System.Text;
using System.Text.Json;
using System.Xml.Linq;

namespace SancMeld.Connectors.UkFcdo;

/// <summary>
/// Turns one &lt;Designation&gt; into the JSON kept in designation.raw_record: elements
/// become properties, repeated elements arrays, leaf elements their text. The same
/// shape the EU connector writes, so a jsonb query over raw records reads both alike.
/// </summary>
internal static class UkRawJson
{
    public static string ToJson(XElement element)
    {
        var buffer = new ArrayBufferWriter<byte>(4096);

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

        foreach (var attribute in element.Attributes().Where(a => !a.IsNamespaceDeclaration))
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
