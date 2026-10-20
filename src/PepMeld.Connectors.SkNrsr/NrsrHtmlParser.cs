using System.Globalization;
using System.Text.RegularExpressions;
using AngleSharp.Dom;
using AngleSharp.Html.Parser;

namespace PepMeld.Connectors.SkNrsr;

/// <summary>A deputy as the alphabetical list of a term names them.</summary>
internal sealed record ListedDeputy(int Id, string ListedName);

/// <summary>A deputy's profile page: the identity fields under "Detaily poslanca".</summary>
internal sealed record DeputyProfile(
    string? TitleBefore,
    string? TitleAfter,
    string GivenName,
    string FamilyName,
    DateOnly? BirthDate,
    string? Residence,
    string? Region,
    string? Nationality);

/// <summary>
/// Reads the two pages the site has per term: the alphabetical list, whose links carry
/// the deputy's id, and the profile, a label-value grid. The site is ASP.NET WebForms
/// with view state and a calendar on every page; nothing of that is read.
/// </summary>
internal static partial class NrsrHtmlParser
{
    private static readonly HtmlParser Parser = new();

    [GeneratedRegex(@"PoslanecID=(\d+)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DeputyId();

    [GeneratedRegex(@"(\d{1,2})\.\s*(\d{1,2})\.\s*(\d{4})", RegexOptions.CultureInvariant)]
    private static partial Regex SlovakDate();

    /// <summary>Titles the site writes after the comma that belong after the name.</summary>
    private static readonly HashSet<string> AfterTitles = new(StringComparer.OrdinalIgnoreCase)
    {
        "CSc.", "PhD.", "Ph.D.", "DrSc.", "MBA", "LL.M.", "LLM", "ArtD.", "MPH", "DBA", "MSc.", "M.A.", "MA", "BSBA", "dr. h. c.",
    };

    public static IReadOnlyList<ListedDeputy> ParseList(string html)
    {
        var document = Parser.ParseDocument(html);
        var result = new List<ListedDeputy>();
        var seen = new HashSet<int>();

        foreach (var link in document.QuerySelectorAll("div.mps_list_block a[href*='PoslanecID=']"))
        {
            var match = DeputyId().Match(link.GetAttribute("href") ?? string.Empty);
            if (!match.Success)
            {
                continue;
            }

            var id = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
            if (seen.Add(id))
            {
                result.Add(new ListedDeputy(id, Clean(link.TextContent)));
            }
        }

        return result;
    }

    public static DeputyProfile? ParseProfile(string html)
    {
        var document = Parser.ParseDocument(html);
        var grid = document.QuerySelector("div.mp_personal_data");
        if (grid is null)
        {
            return null;
        }

        var fields = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var cell in grid.Children)
        {
            var label = cell.QuerySelector("strong")?.TextContent;
            var value = cell.QuerySelector("span")?.TextContent;
            if (label is not null && value is not null)
            {
                fields[Clean(label)] = Clean(value);
            }
        }

        if (!fields.TryGetValue("Meno", out var given) || !fields.TryGetValue("Priezvisko", out var family) || family.Length == 0)
        {
            return null;
        }

        var (before, after) = SplitTitles(fields.GetValueOrDefault("Titul"));

        return new DeputyProfile(
            before,
            after,
            given,
            family,
            ParseDate(fields.GetValueOrDefault("Narodený(á)")),
            Null(fields.GetValueOrDefault("Bydlisko")),
            Null(fields.GetValueOrDefault("Kraj")),
            Null(fields.GetValueOrDefault("Národnosť")));
    }

    /// <summary>"6. 2. 1984" as the site writes it; null for anything else.</summary>
    internal static DateOnly? ParseDate(string? text)
    {
        if (text is null)
        {
            return null;
        }

        var match = SlovakDate().Match(text);
        if (!match.Success)
        {
            return null;
        }

        var day = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        var month = int.Parse(match.Groups[2].Value, CultureInfo.InvariantCulture);
        var year = int.Parse(match.Groups[3].Value, CultureInfo.InvariantCulture);

        return DateOnly.TryParse($"{year:D4}-{month:D2}-{day:D2}", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : null;
    }

    /// <summary>"MUDr., CSc." is one field on the site; CSc. goes after the name, MUDr. before it.</summary>
    internal static (string? Before, string? After) SplitTitles(string? titles)
    {
        if (string.IsNullOrWhiteSpace(titles))
        {
            return (null, null);
        }

        var parts = titles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var before = parts.Where(p => !AfterTitles.Contains(p)).ToList();
        var after = parts.Where(AfterTitles.Contains).ToList();

        return (before.Count > 0 ? string.Join(", ", before) : null, after.Count > 0 ? string.Join(", ", after) : null);
    }

    private static string Clean(string text) =>
        string.Join(' ', text.Replace(' ', ' ').Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

    private static string? Null(string? value) => string.IsNullOrWhiteSpace(value) ? null : value;
}
