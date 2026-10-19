using System.Globalization;
using System.Text.Json;
using SancMeld.Core.Matching;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.SkRegister;

/// <summary>A company an RPO search found: its record id, IČO and every name it has carried.</summary>
public sealed record RpoSearchHit(long Id, string? Ico, IReadOnlyList<string> Names, DateOnly? Terminated);

/// <summary>A shareholder or a member of the statutory body, as RPO records them.</summary>
public sealed record RpoPerson(
    string FullName,
    string? GivenName,
    string? FamilyName,
    RelationKind Role,
    string? Function,
    DateOnly? Start,
    DateOnly? End,
    string? CountryIso2,
    string? Place);

/// <summary>A company as RPO records it: names, people, and where its corporate shareholders sit.</summary>
public sealed record RpoCompany(
    long Id,
    string? Ico,
    string Name,
    DateOnly? Terminated,
    IReadOnlyList<RpoPerson> People,
    IReadOnlyList<string> CorporateOwnerCountries);

/// <summary>
/// Reads RPO's JSON: the search result list and one entity with its history. A person is
/// a natural person (personName); a shareholder with a fullName is a company, of which only
/// the country is kept — who owns a Slovak company from abroad is a question for the
/// foreign register, and the country says which one.
/// </summary>
public static class RpoRecordParser
{
    public static IReadOnlyList<RpoSearchHit> ParseSearch(JsonElement root)
    {
        if (!root.TryGetProperty("results", out var results) || results.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var hits = new List<RpoSearchHit>();
        foreach (var r in results.EnumerateArray())
        {
            if (!r.TryGetProperty("id", out var id) || !id.TryGetInt64(out var entityId))
            {
                continue;
            }

            hits.Add(new RpoSearchHit(entityId, CurrentValue(r, "identifiers"), Values(r, "fullNames"), Date(r, "termination")));
        }

        return hits;
    }

    public static RpoCompany? ParseEntity(JsonElement root)
    {
        if (!root.TryGetProperty("id", out var id) || !id.TryGetInt64(out var entityId))
        {
            return null;
        }

        var name = CurrentValue(root, "fullNames");
        if (name is null)
        {
            return null;
        }

        var people = new List<RpoPerson>();
        var corporate = new List<string>();

        Read(root, "stakeholders", RelationKind.OwnerOf, people, corporate);
        Read(root, "statutoryBodies", RelationKind.DirectorOf, people, corporate);

        return new RpoCompany(entityId, CurrentValue(root, "identifiers"), name, Date(root, "termination"), people, [.. corporate.Distinct(StringComparer.Ordinal)]);
    }

    private static void Read(JsonElement root, string property, RelationKind role, List<RpoPerson> people, List<string> corporate)
    {
        if (!root.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return;
        }

        foreach (var s in list.EnumerateArray())
        {
            var country = s.TryGetProperty("address", out var address) && address.TryGetProperty("country", out var c)
                ? IsoCountryCodes.Alpha2(Str(c, "code"))
                : null;

            if (s.TryGetProperty("personName", out var person) && person.ValueKind == JsonValueKind.Object)
            {
                var given = First(person, "givenNames");
                var family = First(person, "familyNames");
                var full = Str(person, "formatedName") ?? string.Join(' ', new[] { given, family }.Where(x => x is not null));
                if (string.IsNullOrWhiteSpace(full))
                {
                    continue;
                }

                var place = address.ValueKind == JsonValueKind.Object
                    ? string.Join(", ", new[]
                    {
                        address.TryGetProperty("municipality", out var mun) ? Str(mun, "value") : null,
                        address.TryGetProperty("country", out var cn) ? Str(cn, "value") : null,
                    }.Where(x => !string.IsNullOrWhiteSpace(x)))
                    : null;

                people.Add(new RpoPerson(
                    full.Trim(), given, family, role,
                    s.TryGetProperty("stakeholderType", out var type) ? Str(type, "value") : null,
                    Date(s, "validFrom"), Date(s, "validTo"),
                    country, string.IsNullOrEmpty(place) ? null : place));
            }
            else if (country is not null)
            {
                corporate.Add(country);
            }
        }
    }

    /// <summary>The value valid today: the entry without validTo, else the latest.</summary>
    private static string? CurrentValue(JsonElement e, string property)
    {
        if (!e.TryGetProperty(property, out var list) || list.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        string? latest = null;
        foreach (var item in list.EnumerateArray())
        {
            var value = Str(item, "value");
            if (value is null)
            {
                continue;
            }

            if (!item.TryGetProperty("validTo", out var to) || to.ValueKind == JsonValueKind.Null)
            {
                return value.Trim();
            }

            latest = value.Trim();
        }

        return latest;
    }

    private static IReadOnlyList<string> Values(JsonElement e, string property) =>
        e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array
            ? [.. list.EnumerateArray().Select(i => Str(i, "value")).Where(v => v is not null).Select(v => v!.Trim()).Distinct(StringComparer.Ordinal)]
            : [];

    private static string? First(JsonElement e, string property) =>
        e.TryGetProperty(property, out var list) && list.ValueKind == JsonValueKind.Array && list.GetArrayLength() > 0
            ? list[0].GetString()
            : null;

    private static string? Str(JsonElement e, string property) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

    private static DateOnly? Date(JsonElement e, string property) =>
        Str(e, property) is { } s && DateOnly.TryParse(s, CultureInfo.InvariantCulture, out var d) ? d : null;
}
