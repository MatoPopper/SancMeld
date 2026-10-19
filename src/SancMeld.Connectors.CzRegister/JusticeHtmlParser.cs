using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;
using SancMeld.Core.Matching;

namespace SancMeld.Connectors.CzRegister;

/// <summary>One row of the justice.cz person search: who, born when, living where, in which role, in which company.</summary>
public sealed record JusticePersonRow(
    string Name,
    DateOnly? BirthDate,
    string? Address,
    string? CountryIso2,
    string Role,
    string Company,
    string? Ico,
    bool CompanyDeleted,
    string? SubjectId);

/// <summary>What one search page says: its rows, and whether the register cut the list short.</summary>
public sealed record JusticeSearchPage(IReadOnlyList<JusticePersonRow> Rows, bool Truncated, bool Captcha);

/// <summary>
/// Reads the person search page of the Czech public register. The register has no API;
/// the markup (application version 15.9, 2026-10-19) is a table per hit with labelled
/// cells, which is read by label so that a reordered column does not break it. A change
/// that does break it shows as no rows at all, never as wrong rows.
/// </summary>
public static partial class JusticeHtmlParser
{
    private static readonly string[] Months =
    [
        "ledna", "února", "března", "dubna", "května", "června", "července", "srpna", "září", "října", "listopadu", "prosince",
    ];

    public static JusticeSearchPage Parse(string html)
    {
        ArgumentNullException.ThrowIfNull(html);

        if (html.Contains("captcha", StringComparison.OrdinalIgnoreCase) && !html.Contains("search-results", StringComparison.Ordinal))
        {
            return new JusticeSearchPage([], false, Captcha: true);
        }

        var rows = new List<JusticePersonRow>();
        foreach (Match item in Item().Matches(html))
        {
            var cells = Cells(item.Value);
            if (!cells.TryGetValue("Jméno", out var name) || !cells.TryGetValue("Název subjektu", out var company))
            {
                continue;
            }

            var address = cells.GetValueOrDefault("Adresa");
            var ico = cells.TryGetValue("IČO", out var icoText) ? new string(icoText.Where(char.IsAsciiDigit).ToArray()) : null;
            var subject = SubjectId().Match(item.Value);

            rows.Add(new JusticePersonRow(
                Undouble(name),
                Date(cells.GetValueOrDefault("Datum narození")),
                address,
                Country(address),
                cells.GetValueOrDefault("Angažmá") ?? string.Empty,
                company.Replace("Vymazáno", string.Empty, StringComparison.Ordinal).Trim(),
                ico is { Length: >= 6 and <= 8 } ? ico.PadLeft(8, '0') : null,
                item.Value.Contains("subject-state vymazano", StringComparison.Ordinal),
                subject.Success ? subject.Groups["id"].Value : null));
        }

        var count = Count().Match(html);
        var truncated = count.Success && count.Groups["n"].Value.Contains("prvních", StringComparison.Ordinal);
        return new JusticeSearchPage(rows, truncated, Captcha: false);
    }

    /// <summary>"30. června 1948" → 1948-06-30.</summary>
    public static DateOnly? Date(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var m = CzechDate().Match(text);
        if (!m.Success)
        {
            return null;
        }

        var month = Array.IndexOf(Months, m.Groups["m"].Value.ToLowerInvariant()) + 1;
        return month > 0
            && int.TryParse(m.Groups["d"].Value, CultureInfo.InvariantCulture, out var day)
            && int.TryParse(m.Groups["y"].Value, CultureInfo.InvariantCulture, out var year)
            && day is >= 1 and <= 31
            ? new DateOnly(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)))
            : null;
    }

    /// <summary>The country at the end of a register address; a Czech address names none.</summary>
    public static string? Country(string? address)
    {
        if (string.IsNullOrWhiteSpace(address))
        {
            return null;
        }

        var last = address.Split(',').Last().Trim();
        return CzCountryNames.Find(last)?.Iso2 ?? "CZ";
    }

    /// <summary>The register prints the name twice ("Andreas Schneider Andreas Schneider"); once is enough.</summary>
    private static string Undouble(string name)
    {
        var words = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length % 2 == 0)
        {
            var half = words.Length / 2;
            if (words.Take(half).SequenceEqual(words.Skip(half), StringComparer.Ordinal))
            {
                return string.Join(' ', words.Take(half));
            }
        }

        return string.Join(' ', words);
    }

    private static Dictionary<string, string> Cells(string item)
    {
        var cells = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match m in Cell().Matches(item))
        {
            var label = Text(m.Groups["th"].Value).TrimEnd(':');
            var value = Text(m.Groups["td"].Value);
            if (label.Length > 0 && !cells.ContainsKey(label))
            {
                cells[label] = value;
            }
        }

        return cells;
    }

    private static string Text(string fragment) =>
        string.Join(' ', WebUtility.HtmlDecode(Tags().Replace(fragment, " ")).Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    [GeneratedRegex("""<li class="result (?:even|odd)">.*?</li>""", RegexOptions.Singleline)]
    private static partial Regex Item();

    [GeneratedRegex("""<th[^>]*>(?<th>.*?)</th>\s*<td[^>]*>(?<td>.*?)</td>""", RegexOptions.Singleline)]
    private static partial Regex Cell();

    [GeneratedRegex("""subjektId=(?<id>\d+)""")]
    private static partial Regex SubjectId();

    [GeneratedRegex("""Počet nalezených subjektů:\s*<span>(?<n>[^<]*)</span>""")]
    private static partial Regex Count();

    [GeneratedRegex("""(?<d>\d{1,2})\.\s*(?<m>\p{L}+)\s+(?<y>\d{4})""")]
    private static partial Regex CzechDate();

    [GeneratedRegex("<[^>]+>")]
    private static partial Regex Tags();
}
