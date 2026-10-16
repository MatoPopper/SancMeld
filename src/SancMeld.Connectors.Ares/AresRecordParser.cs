using System.Globalization;
using System.Text.Json;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.Ares;

/// <summary>A Czech company as ARES's public register records it, reduced to what a sanctions question needs.</summary>
public sealed record AresCompany(
    string Ico,
    string Name,
    string? CountryIso2,
    IReadOnlyList<AresPerson> People,
    IReadOnlyList<AresLegalHolder> Holders);

/// <summary>
/// A natural person in a role: a shareholder (with a share) or a member of a statutory
/// body. One row per person and role, the history merged: Andrey Shnayder was a
/// shareholder 2019–2023, again 2023–2026, and from 2026, and that is one tie since 2019.
/// </summary>
public sealed record AresPerson(
    string GivenName,
    string Surname,
    DateOnly BirthDate,
    RelationKind Role,
    decimal? SharePercent,
    string? Function,
    DateOnly? Start,
    DateOnly? End)
{
    public string FullName => $"{GivenName} {Surname}".Trim();
}

/// <summary>A company that holds a share in the one being read.</summary>
public sealed record AresLegalHolder(
    string Name,
    string? Ico,
    string? CountryIso2,
    decimal? SharePercent,
    DateOnly? Start,
    DateOnly? End);

/// <summary>
/// Reads the JSON of /ekonomicke-subjekty-vr/{ico}. The response carries one record per
/// court register it appears in; the primary one is read. Shareholders sit under
/// "spolecnici" (s.r.o.) or "akcionari" (a.s.), statutory members under
/// "statutarniOrgany", and every entry has a registration date and, once removed, a
/// deletion date — so ended ties are kept, dated, rather than dropped.
/// </summary>
public static class AresRecordParser
{
    public static AresCompany? Parse(JsonElement root)
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

        if (primary is not { } z)
        {
            return null;
        }

        // The IČO is a history like every other field ([{datumZapisu, hodnota}]); the
        // response also carries it once at the top as icoId.
        var ico = Current(z, "ico") ?? Str(z, "ico") ?? Str(root, "icoId") ?? string.Empty;
        var name = Current(z, "obchodniJmeno") ?? ico;
        string? country = null;
        if (z.TryGetProperty("adresy", out var addresses) && addresses.ValueKind == JsonValueKind.Array)
        {
            foreach (var a in addresses.EnumerateArray())
            {
                if (!a.TryGetProperty("datumVymazu", out _) && a.TryGetProperty("adresa", out var adr))
                {
                    country = Str(adr, "kodStatu");
                    break;
                }
            }
        }

        var people = new List<AresPerson>();
        var holders = new List<AresLegalHolder>();

        foreach (var key in new[] { "spolecnici", "akcionari" })
        {
            foreach (var organ in Array(z, key))
            {
                foreach (var entry in Array(organ, "spolecnik").Concat(Array(organ, "akcionar")).Concat(Array(organ, "clenoveOrganu")))
                {
                    if (!entry.TryGetProperty("osoba", out var osoba))
                    {
                        osoba = entry;
                    }

                    var start = Date(entry, "datumZapisu");
                    var end = Date(entry, "datumVymazu");
                    var share = Share(entry);

                    if (osoba.TryGetProperty("fyzickaOsoba", out var fo) && Person(fo) is { } p)
                    {
                        people.Add(new AresPerson(p.Given, p.Surname, p.Born, RelationKind.OwnerOf, share, null, start, end));
                    }
                    else if (osoba.TryGetProperty("pravnickaOsoba", out var po) && Str(po, "obchodniJmeno") is { } holderName)
                    {
                        holders.Add(new AresLegalHolder(holderName, Str(po, "ico"),
                            po.TryGetProperty("adresa", out var hadr) ? Str(hadr, "kodStatu") : null, share, start, end));
                    }
                }
            }
        }

        foreach (var organ in Array(z, "statutarniOrgany"))
        {
            foreach (var member in Array(organ, "clenoveOrganu"))
            {
                if (member.TryGetProperty("fyzickaOsoba", out var fo) && Person(fo) is { } p)
                {
                    string? function = null;
                    if (member.TryGetProperty("clenstvi", out var c) && c.TryGetProperty("funkce", out var f))
                    {
                        function = Str(f, "nazev");
                    }

                    people.Add(new AresPerson(p.Given, p.Surname, p.Born, RelationKind.DirectorOf, null, function,
                        Date(member, "datumZapisu"), Date(member, "datumVymazu")));
                }
            }
        }

        return new AresCompany(ico, name, country ?? "CZ", Merge(people), holders);
    }

    /// <summary>
    /// One row per person and role: earliest start, open-ended if any stint is current,
    /// and the share of the current stint (or the last one).
    /// </summary>
    private static List<AresPerson> Merge(List<AresPerson> people) =>
    [
        .. people
            .GroupBy(p => (Given: p.GivenName.ToUpperInvariant(), Surname: p.Surname.ToUpperInvariant(), p.BirthDate, p.Role))
            .Select(g =>
            {
                var current = g.Where(p => p.End is null).ToList();
                var latest = current.Count > 0 ? current.OrderByDescending(p => p.Start).First() : g.OrderByDescending(p => p.End).First();
                return latest with
                {
                    Start = g.Min(p => p.Start),
                    End = current.Count > 0 ? null : g.Max(p => p.End),
                };
            }),
    ];

    private static (string Given, string Surname, DateOnly Born)? Person(JsonElement fo)
    {
        var born = Date(fo, "datumNarozeni");
        var surname = Str(fo, "prijmeni");
        if (born is null || string.IsNullOrWhiteSpace(surname))
        {
            // Without a date of birth there is nothing to match on safely.
            return null;
        }

        return (Str(fo, "jmeno") ?? string.Empty, surname, born.Value);
    }

    /// <summary>"76", "61;5" (ARES writes some fractions with a semicolon) → percent.</summary>
    private static decimal? Share(JsonElement entry)
    {
        // The current share, or for an ended stake the last one it had: ordered by
        // registration date, not by the array, whose order ARES does not promise.
        JsonElement? chosen = null;
        DateOnly? chosenDate = null;
        foreach (var podil in Array(entry, "podil"))
        {
            var current = !podil.TryGetProperty("datumVymazu", out _);
            var date = Date(podil, "datumZapisu") ?? DateOnly.MinValue;
            var chosenCurrent = chosen is { } c0 && !c0.TryGetProperty("datumVymazu", out _);

            if (chosen is null || (current && !chosenCurrent) || (current == chosenCurrent && date > chosenDate))
            {
                chosen = podil;
                chosenDate = date;
            }
        }

        if (chosen is not { } c || !c.TryGetProperty("velikostPodilu", out var size))
        {
            return null;
        }

        if (Str(size, "typObnos") is { } type && type != "PROCENTA")
        {
            return null;
        }

        var text = Str(size, "hodnota")?.Replace(';', '.').Replace(',', '.');
        return decimal.TryParse(text, NumberStyles.Number, CultureInfo.InvariantCulture, out var v) ? v : null;
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
