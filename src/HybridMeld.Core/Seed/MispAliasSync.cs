using System.Text.Json;
using HybridMeld.Core.Data;
using HybridMeld.Core.Sources;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SancMeld.Core.Matching;

namespace HybridMeld.Core.Seed;

public sealed record AliasSyncOutcome(int Clusters, int ActorsMatched, int AliasesAdded);

/// <summary>
/// Names the security community uses for the actors the sanctions lists name by unit:
/// the MISP threat-actor galaxy (https://github.com/MISP/misp-galaxy, CC0) keeps, per
/// cluster, a value ("Sandworm") and its synonyms ("Seashell Blizzard", "APT44", "G0034",
/// the MITRE id). Where one of them is already a name or an alias of an actor here, as
/// the EU list's own aliases are ("Sandworm Team" on GRU unit 74455, "FANCY BEAR" on
/// 26165), the rest of the cluster becomes aliases with origin "misp", so a search for
/// "Seashell Blizzard" reaches the sanctioned unit. Nothing is matched on a guess: the
/// term must equal a name, or be that name without a trailing "team"/"group"/"apt"
/// qualifier.
/// </summary>
public sealed class MispAliasSync(HybridMeldDbContext db, IHttpClientFactory clients, TimeProvider clock, ILogger<MispAliasSync> logger)
{
    public const string GalaxyUrl = "https://raw.githubusercontent.com/MISP/misp-galaxy/main/clusters/threat-actor.json";

    private static readonly string[] Qualifiers = ["team", "group", "apt", "advanced persistent threat"];

    public async Task<AliasSyncOutcome> SyncAsync(CancellationToken ct)
    {
        var client = clients.CreateClient(PageFetcher.HttpClientName);
        using var stream = await client.GetStreamAsync(GalaxyUrl, ct).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
        var clusters = ParseClusters(document.RootElement);
        return await ApplyAsync(clusters, ct).ConfigureAwait(false);
    }

    /// <summary>One cluster: every name it goes by, value first, and where it comes from.</summary>
    public sealed record Cluster(string Value, IReadOnlyList<string> Names, string? SourceUrl);

    public static IReadOnlyList<Cluster> ParseClusters(JsonElement root)
    {
        var clusters = new List<Cluster>();
        if (!root.TryGetProperty("values", out var values))
        {
            return clusters;
        }

        foreach (var item in values.EnumerateArray())
        {
            if (!item.TryGetProperty("value", out var valueElement) || valueElement.GetString() is not { Length: > 0 } value)
            {
                continue;
            }

            var names = new List<string> { value };
            string? url = null;
            if (item.TryGetProperty("meta", out var meta))
            {
                if (meta.TryGetProperty("synonyms", out var synonyms) && synonyms.ValueKind == JsonValueKind.Array)
                {
                    names.AddRange(synonyms.EnumerateArray().Select(s => s.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)).Select(s => s!));
                }

                if (meta.TryGetProperty("refs", out var refs) && refs.ValueKind == JsonValueKind.Array)
                {
                    url = refs.EnumerateArray().Select(r => r.GetString()).FirstOrDefault(r => r?.StartsWith("https://attack.mitre.org/", StringComparison.Ordinal) == true)
                        ?? refs.EnumerateArray().Select(r => r.GetString()).FirstOrDefault();
                }
            }

            clusters.Add(new Cluster(value, names.Distinct(StringComparer.OrdinalIgnoreCase).ToList(), url));
        }

        return clusters;
    }

    /// <summary>Whether a galaxy term names the same thing as a stored name: equal, or the stored name is the term plus a qualifier.</summary>
    public static bool Matches(string termNormalized, string storedNormalized)
    {
        if (termNormalized.Length < 4)
        {
            return false;
        }

        if (termNormalized == storedNormalized)
        {
            return true;
        }

        if (!storedNormalized.StartsWith(termNormalized + " ", StringComparison.Ordinal))
        {
            return false;
        }

        var rest = storedNormalized[(termNormalized.Length + 1)..];
        return Qualifiers.Contains(rest, StringComparer.Ordinal);
    }

    public async Task<AliasSyncOutcome> ApplyAsync(IReadOnlyList<Cluster> clusters, CancellationToken ct)
    {
        var actors = await db.Actors.Include(a => a.Aliases).ToListAsync(ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var matched = new HashSet<Guid>();
        var added = 0;

        // Only names that did not come from the galaxy count as a match. The galaxy shares
        // terms between clusters ("Grizzly Steppe" is on APT28 and on APT29), so matching on
        // an alias this sync added would chain the SVR's names onto a GRU unit; it did, on
        // the first run (2026-10-21), before this line.
        var anchors = actors.ToDictionary(
            a => a.Id,
            a => a.Aliases.Where(x => x.Origin != "misp").Select(x => x.NameNormalized).Append(a.NameNormalized).ToList());

        foreach (var cluster in clusters)
        {
            var terms = cluster.Names.Select(n => (Original: n, Normalized: NameNormalizer.Normalize(n))).Where(t => t.Normalized.Length > 0).ToList();

            foreach (var actor in actors)
            {
                var stored = anchors[actor.Id];
                if (!terms.Any(t => stored.Any(s => Matches(t.Normalized, s))))
                {
                    continue;
                }

                matched.Add(actor.Id);
                foreach (var term in terms)
                {
                    if (term.Normalized == actor.NameNormalized || actor.Aliases.Any(a => a.NameNormalized == term.Normalized))
                    {
                        continue;
                    }

                    actor.Aliases.Add(new ActorAlias
                    {
                        ActorId = actor.Id,
                        Name = term.Original,
                        NameNormalized = term.Normalized,
                        Origin = "misp",
                        SourceUrl = cluster.SourceUrl ?? GalaxyUrl,
                        CreatedAt = now,
                    });
                    added++;
                }
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("MISP galaxy: {Clusters} clusters, {Actors} actors matched, {Aliases} aliases added.", clusters.Count, matched.Count, added);
        return new AliasSyncOutcome(clusters.Count, matched.Count, added);
    }
}
