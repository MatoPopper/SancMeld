using System.Globalization;
using System.Text.Json;

namespace SancMeld.Connectors.Rpvs;

/// <summary>A partner of the public sector: the register entry, its company, and its beneficial owners.</summary>
public sealed record RpvsPartner(int Id, int? FileNumber, RpvsCompany? Company, IReadOnlyList<RpvsOwner> Owners);

/// <summary>The company (or sole trader) behind a partner entry, as last registered.</summary>
public sealed record RpvsCompany(string Name, string? Ico, string? Form);

/// <summary>A beneficial owner: always a natural person, with a date of birth on the newer entries.</summary>
public sealed record RpvsOwner(
    string GivenName,
    string Surname,
    DateOnly? BirthDate,
    bool IsPublicOfficial,
    string? Nationality,
    DateOnly? Start,
    DateOnly? End)
{
    public string FullName => $"{GivenName} {Surname}".Trim();
}

/// <summary>One page of the Partneri feed and where the next one is.</summary>
public sealed record RpvsPage(IReadOnlyList<RpvsPartner> Partners, string? NextLink);

/// <summary>
/// Reads a page of the RPVS OData feed (Partneri, with PartneriVerejnehoSektora and
/// KonecniUzivateliaVyhod expanded). Dates come as midnight in Bratislava with its
/// offset; the calendar date is the one in that offset, never shifted to UTC — a birthday
/// moved to the previous day would match nobody.
/// </summary>
public static class RpvsRecordParser
{
    public static RpvsPage Parse(JsonElement root)
    {
        var partners = new List<RpvsPartner>();

        if (root.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.Array)
        {
            foreach (var p in value.EnumerateArray())
            {
                if (Int(p, "Id") is not { } id)
                {
                    continue;
                }

                partners.Add(new RpvsPartner(id, Int(p, "CisloVlozky"), Company(p), Owners(p)));
            }
        }

        return new RpvsPage(partners, Text(root, "@odata.nextLink"));
    }

    /// <summary>The current company entry; a partner whose entries all ended keeps its latest one.</summary>
    private static RpvsCompany? Company(JsonElement partner)
    {
        if (!partner.TryGetProperty("PartneriVerejnehoSektora", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        JsonElement? best = null;
        foreach (var e in entries.EnumerateArray())
        {
            if (best is not { } current)
            {
                best = e;
                continue;
            }

            var (end, currentEnd) = (Date(e, "PlatnostDo"), Date(current, "PlatnostDo"));
            if ((end is null && currentEnd is not null) || (end is { } a && currentEnd is { } b && a > b))
            {
                best = e;
            }
        }

        if (best is not { } chosen)
        {
            return null;
        }

        var name = Text(chosen, "ObchodneMeno") ?? $"{Text(chosen, "Meno")} {Text(chosen, "Priezvisko")}".Trim();
        return string.IsNullOrWhiteSpace(name)
            ? null
            : new RpvsCompany(name.Trim(), Text(chosen, "Ico") is { Length: > 0 } ico ? ico.Trim() : null, Text(chosen, "FormaOsoby"));
    }

    private static List<RpvsOwner> Owners(JsonElement partner)
    {
        var owners = new List<RpvsOwner>();
        if (!partner.TryGetProperty("KonecniUzivateliaVyhod", out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return owners;
        }

        foreach (var o in list.EnumerateArray())
        {
            var given = Text(o, "Meno")?.Trim() ?? string.Empty;
            var surname = Text(o, "Priezvisko")?.Trim() ?? string.Empty;
            if (given.Length == 0 && surname.Length == 0)
            {
                continue;
            }

            string? nationality = null;
            if (o.TryGetProperty("StatnaPrislusnost", out var state) && state.ValueKind == JsonValueKind.Object)
            {
                nationality = Text(state, "Meno");
            }

            owners.Add(new RpvsOwner(
                given,
                surname,
                Date(o, "DatumNarodenia"),
                o.TryGetProperty("JeVerejnyCinitel", out var pep) && pep.ValueKind == JsonValueKind.True,
                nationality,
                Date(o, "PlatnostOd"),
                Date(o, "PlatnostDo")));
        }

        return owners;
    }

    private static string? Text(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int? Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var i) ? i : null;

    private static DateOnly? Date(JsonElement e, string name) =>
        Text(e, name) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.None, out var dto)
            ? DateOnly.FromDateTime(dto.DateTime)
            : null;
}
