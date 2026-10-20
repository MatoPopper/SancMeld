using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PepMeld.Connectors.SkNrsrVnf;

/// <summary>An official as the alphabetical list names them, with the site's own identifier.</summary>
internal sealed record ListedOfficial(string UserId, string ListedName);

/// <summary>What the latest declaration page says: the name as filed, the year, the functions held.</summary>
internal sealed record Declaration(string HeaderName, int? Year, IReadOnlyList<string> Functions);

/// <summary>
/// Reads the register's two pages. The list is a WebForms page whose alphabetical view
/// comes back from a form post; the declaration is a label/value table in which the
/// value row follows the label row. Nothing else on either page is read.
/// </summary>
internal static partial class VnfHtmlParser
{
    private static readonly HtmlParser Parser = new();

    [GeneratedRegex(@"UserId=([^&""]+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UserId();

    /// <summary>The hidden fields a WebForms post must echo: view state and its validation.</summary>
    public static IReadOnlyDictionary<string, string> HiddenFields(string html)
    {
        var document = Parser.ParseDocument(html);
        var fields = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var input in document.QuerySelectorAll("input[type='hidden'][name]"))
        {
            fields[input.GetAttribute("name")!] = input.GetAttribute("value") ?? string.Empty;
        }

        return fields;
    }

    public static IReadOnlyList<ListedOfficial> ParseList(string html)
    {
        var document = Parser.ParseDocument(html);
        var result = new List<ListedOfficial>();
        var seen = new HashSet<string>(StringComparer.Ordinal);

        foreach (var link in document.QuerySelectorAll("a[href*='sid=vnf/oznamenie']"))
        {
            var match = UserId().Match(link.GetAttribute("href") ?? string.Empty);
            if (match.Success && seen.Add(match.Groups[1].Value))
            {
                result.Add(new ListedOfficial(Uri.UnescapeDataString(match.Groups[1].Value), Clean(link.TextContent)));
            }
        }

        return result;
    }

    public static Declaration? ParseDeclaration(string html)
    {
        var document = Parser.ParseDocument(html);

        string? header = null;
        int? year = null;
        List<string>? functions = null;

        foreach (var label in document.QuerySelectorAll("td.label"))
        {
            var text = Clean(label.TextContent).TrimEnd(':');
            var value = label.ParentElement?.NextElementSibling?.QuerySelector("td.value");
            if (value is null)
            {
                continue;
            }

            if (text.StartsWith("titul, meno, priezvisko", StringComparison.OrdinalIgnoreCase))
            {
                header = Clean(value.TextContent);
            }
            else if (text.StartsWith("oznámenie za rok", StringComparison.OrdinalIgnoreCase))
            {
                year = int.TryParse(Clean(value.TextContent), NumberStyles.Integer, CultureInfo.InvariantCulture, out var y) ? y : null;
            }
            else if (text.StartsWith("vykonávaná verejná funkcia", StringComparison.OrdinalIgnoreCase))
            {
                functions = value.QuerySelectorAll("div").Select(d => Clean(d.TextContent)).Where(f => f.Length > 0).ToList();
                if (functions.Count == 0 && Clean(value.TextContent) is { Length: > 0 } single)
                {
                    functions.Add(single);
                }
            }
        }

        return header is null ? null : new Declaration(header, year, functions ?? []);
    }

    /// <summary>
    /// "doc. JUDr. BORIS BALOG, PhD.": the name is the run of upper-case words, what
    /// stands before it are titles before the name, what follows the comma titles after.
    /// </summary>
    internal static (string? Before, string? After) Titles(string header)
    {
        var comma = header.IndexOf(',', StringComparison.Ordinal);
        var head = comma >= 0 ? header[..comma] : header;
        var after = comma >= 0 ? Clean(header[(comma + 1)..]) : null;

        var words = head.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var firstName = Array.FindIndex(words, IsUpperCaseWord);
        var before = firstName > 0 ? string.Join(' ', words[..firstName]) : null;

        return (Null(before), Null(after));
    }

    private static bool IsUpperCaseWord(string word) =>
        word.Length > 1 && word.Any(char.IsLetter) && !word.Any(char.IsLower);

    private static string Clean(string text) =>
        string.Join(' ', text.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
