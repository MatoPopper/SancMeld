using System.Net;
using System.Text.RegularExpressions;

namespace SancMeld.Connectors.SkRegister;

/// <summary>One row of an ORSR person search: who, in which company, and the extract's ids.</summary>
public sealed record OrsrPersonRow(string Name, string Company, string Id, string Sid);

/// <summary>
/// Reads the three ORSR pages this connector needs: the person search, its record count,
/// and the IČO on a company extract. ORSR has no API; the markup has been stable for
/// years, and a change breaks the parser loudly (no rows, no IČO), never quietly.
/// </summary>
public static partial class OrsrHtmlParser
{
    /// <summary>The rows of a person search page, as many as the page shows (20 at most).</summary>
    public static IReadOnlyList<OrsrPersonRow> PersonRows(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        return
        [
            .. Row().Matches(html).Select(m => new OrsrPersonRow(
                Text(m.Groups["name"].Value),
                Text(m.Groups["company"].Value),
                m.Groups["id"].Value,
                m.Groups["sid"].Value)),
        ];
    }

    /// <summary>The total number of records the search found, from "Záznamy: 1 - 20 / 143".</summary>
    public static int Total(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var at = html.IndexOf("Záznamy:", StringComparison.Ordinal);
        if (at < 0)
        {
            return 0;
        }

        var m = TotalCount().Match(html, at);
        return m.Success && int.TryParse(m.Groups["n"].Value, out var n) ? n : 0;
    }

    /// <summary>The company's IČO from its extract, digits only, or null.</summary>
    public static string? Ico(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        var m = IcoValue().Match(html);
        if (!m.Success)
        {
            return null;
        }

        var digits = new string(m.Groups["ico"].Value.Where(char.IsAsciiDigit).ToArray());
        return digits.Length is >= 6 and <= 8 ? digits.PadLeft(8, '0') : null;
    }

    /// <summary>The full extract, with every past shareholder and director: the link a reader follows.</summary>
    public static string ExtractUrl(string baseUrl, string id, string sid) =>
        $"{baseUrl.TrimEnd('/')}/vypis.asp?ID={id}&SID={sid}&P=1";

    private static string Text(string fragment) =>
        string.Join(' ', WebUtility.HtmlDecode(Tags().Replace(fragment, " "))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex("""<div class="sbj">(?<name>.*?)</div>.*?<a href="vypis\.asp\?ID=(?<id>\d+)&amp;SID=(?<sid>\d+)&amp;P=0"[^>]*>(?<company>.*?)</a>""", RegexOptions.Singleline)]
    private static partial Regex Row();

    [GeneratedRegex("""/&nbsp;(?<n>\d+)""")]
    private static partial Regex TotalCount();

    [GeneratedRegex("""IČO:&nbsp;</span>.*?<span class='ra'>(?<ico>[\d\s]+)</span>""", RegexOptions.Singleline)]
    private static partial Regex IcoValue();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
