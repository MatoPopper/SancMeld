using System.Reflection;
using System.Text.Json;

namespace PepMeld.Connectors.CzAres;

/// <summary>A company the state controls, and why it is on the list.</summary>
public sealed record StateEntity(string Ico, string Name, string Why);

/// <summary>
/// The companies the state owns as a shareholder, kept by hand in StateEntities.json.
/// ARES cannot be searched by shareholder, and the Ministry of Finance's annual report on
/// the state's ownership policy is a PDF; this list is the part of it that matters,
/// each entry checked against the register's shareholder record on the date noted.
/// Add a row when a company is founded or bought, never remove one: a sold company's
/// past seats still count for a year.
/// </summary>
public static class StateEntities
{
    public static IReadOnlyList<StateEntity> Load()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("StateEntities.json")
            ?? throw new InvalidOperationException("StateEntities.json is not embedded.");

        return JsonSerializer.Deserialize<List<StateEntity>>(stream, new JsonSerializerOptions(JsonSerializerDefaults.Web) { ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true })
            ?? [];
    }
}
