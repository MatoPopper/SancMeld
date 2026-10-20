using System.Globalization;
using System.Text.Json;

namespace PepMeld.Connectors.CzAres;

/// <summary>A company as its public-register record names it.</summary>
internal sealed record AresEntity(string Ico, string Name, string? LegalForm);

/// <summary>One natural person's seat in one body of one company, with the register's dates.</summary>
internal sealed record AresSeat(
    string Ico,
    string CompanyName,
    string Body,
    string Role,
    string GivenName,
    string Surname,
    string? TitleBefore,
    string? TitleAfter,
    DateOnly BirthDate,
    string? Residence,
    DateOnly? From,
    DateOnly? To);

/// <summary>
/// Reads /ekonomicke-subjekty-vr/{ico} for the people who run and oversee a company.
/// Statutory bodies sit under "statutarniOrgany"; the supervisory and administrative
/// boards under "ostatniOrgany", which SancMeld's reader leaves alone because a sanctions
/// question is about owners. Procuration and liquidators are not bodies and are skipped.
/// Every member carries the date of birth, which is what makes these seats level A.
/// </summary>
internal static class AresBoardParser
{
    private static readonly string[] BodyWords = ["dozorčí", "správní", "kontrolní", "statutární", "představenstvo", "jednatel"];
    private static readonly string[] SkippedWords = ["prokura", "likvidátor", "výbor pro audit"];

    public static AresEntity? Entity(JsonElement root)
    {
        if (Primary(root) is not { } z)
        {
            return null;
        }

        var ico = Current(z, "ico") ?? Str(root, "icoId") ?? string.Empty;
        var name = Current(z, "obchodniJmeno") ?? ico;
        return new AresEntity(ico, name, Current(z, "pravniForma"));
    }

    public static IReadOnlyList<AresSeat> Seats(JsonElement root)
    {
        if (Primary(root) is not { } z || Entity(root) is not { } entity)
        {
            return [];
        }

        var seats = new List<AresSeat>();

        foreach (var organ in Array(z, "statutarniOrgany").Concat(Array(z, "ostatniOrgany")))
        {
            var body = Str(organ, "nazevOrganu") ?? string.Empty;
            var lower = body.ToLowerInvariant();

            if (SkippedWords.Any(lower.Contains) || !BodyWords.Any(lower.Contains))
            {
                continue;
            }

            foreach (var member in Array(organ, "clenoveOrganu"))
            {
                if (!member.TryGetProperty("fyzickaOsoba", out var person))
                {
                    continue;
                }

                var born = Date(person, "datumNarozeni");
                var surname = Str(person, "prijmeni");
                if (born is null || string.IsNullOrWhiteSpace(surname))
                {
                    // Without a date of birth there is nothing to match on safely.
                    continue;
                }

                JsonElement function = default, membership = default;
                if (member.TryGetProperty("clenstvi", out var c))
                {
                    c.TryGetProperty("funkce", out function);
                    c.TryGetProperty("clenstvi", out membership);
                }

                var role = Str(function, "nazev") ?? Str(member, "nazevAngazma") ?? body;

                seats.Add(new AresSeat(
                    entity.Ico,
                    entity.Name,
                    body,
                    role,
                    Title(Str(person, "jmeno")),
                    Title(surname),
                    Str(person, "titulPredJmenem"),
                    Str(person, "titulZaJmenem"),
                    born.Value,
                    person.TryGetProperty("adresa", out var address) ? Str(address, "nazevObce") : null,
                    Date(function, "vznikFunkce") ?? Date(membership, "vznikClenstvi") ?? Date(member, "datumZapisu"),
                    Date(function, "zanikFunkce") ?? Date(membership, "zanikClenstvi") ?? Date(member, "datumVymazu")));
            }
        }

        return seats;
    }

    /// <summary>The register writes names in capitals ("DALIBOR ŠAFAŘÍK"); shown as a name, not a heading.</summary>
    internal static string Title(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return string.Empty;
        }

        var culture = CultureInfo.GetCultureInfo("cs-CZ");
        return string.Join(' ', name.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => string.Join('-', w.Split('-').Select(part => part.Length == 0 ? part : culture.TextInfo.ToUpper(part[..1]) + culture.TextInfo.ToLower(part[1..])))));
    }

    private static JsonElement? Primary(JsonElement root)
    {
        if (!root.TryGetProperty("zaznamy", out var records) || records.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? primary = null;
        foreach (var r in records.EnumerateArray())
        {
            if (primary is null || (r.TryGetProperty("primarniZaznam", out var p) && p.ValueKind == JsonValueKind.True))
            {
                primary = r;
            }
        }

        return primary;
    }

    private static string? Current(JsonElement z, string key)
    {
        string? last = null;
        foreach (var e in Array(z, key))
        {
            var value = Str(e, "hodnota");
            if (!e.TryGetProperty("datumVymazu", out _) && value is not null)
            {
                return value;
            }

            last = value ?? last;
        }

        return last;
    }

    private static IEnumerable<JsonElement> Array(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array
            ? a.EnumerateArray()
            : [];

    private static string? Str(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateOnly? Date(JsonElement e, string key) =>
        Str(e, key) is { Length: >= 10 } s && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;
}
