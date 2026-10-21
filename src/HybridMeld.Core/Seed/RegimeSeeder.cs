using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Links;
using HybridMeld.Core.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using SancMeld.Core.Matching;

namespace HybridMeld.Core.Seed;

public sealed record SeedOutcome(string Programme, string SourceCode, int Designations, int ActorsNew, int ClaimsNew, int Stale);

/// <summary>
/// The day-one content, with no legal risk in it: every active subject of the EU
/// "destabilising activities of Russia" regime (Council Regulation (EU) 2024/2642,
/// programme RUSDA in EU_FSF, mirrored in CZ_MZV) and of the cyber regime (2019/796,
/// programme CYB in EU_FSF and the UK list) becomes an actor linked to its listing, in
/// one case per regime and source, with one level-A claim "listed under" and the
/// publisher's own reasons as the quote where the list carries them.
///
/// Idempotent: an actor is found by its designation id, a claim by (case, actor,
/// ListedUnder); re-running after an import adds the new listings and nothing else.
/// Reads SancMeld's database with the same connection the grant is read from.
/// </summary>
public sealed class RegimeSeeder(HybridMeldDbContext db, IConfiguration configuration, TimeProvider clock, ILogger<RegimeSeeder> logger)
{
    /// <summary>What is seeded by default: the two thematic regimes that are hybrid threats by definition.</summary>
    public static readonly IReadOnlyList<(string Programme, CampaignKind Kind, string TitleSk, string SummarySk)> Regimes =
    [
        ("RUSDA", CampaignKind.Fimi,
            "Režim EÚ: destabilizačné aktivity Ruska (nar. 2024/2642)",
            "Subjekty zaradené pod sankčný režim EÚ proti hybridným hrozbám Ruska: informačná manipulácia, sabotáž, zasahovanie do volieb a ich sprostredkovatelia. Zdroj je samotný sankčný zoznam; každé tvrdenie tu je úroveň A."),
        ("CYB", CampaignKind.Cyber,
            "Režim EÚ a UK: kybernetické útoky (nar. 2019/796)",
            "Osoby a jednotky zaradené pod kybernetický sankčný režim: GRU 26165 a 74455, ich dôstojníci a prostredníci. Zdroj je samotný sankčný zoznam; každé tvrdenie tu je úroveň A."),
    ];

    public async Task<IReadOnlyList<SeedOutcome>> SeedAsync(IReadOnlyList<string>? programmes, string by, CancellationToken ct)
    {
        var connectionString = configuration.GetConnectionString(LinkResolver.SanctionsConnectionName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException($"ConnectionStrings:{LinkResolver.SanctionsConnectionName} is not configured; the seed reads the sanctions database.");
        }

        var wanted = Regimes.Where(r => programmes is null || programmes.Contains(r.Programme, StringComparer.OrdinalIgnoreCase)).ToList();
        var outcomes = new List<SeedOutcome>();

        foreach (var regime in wanted)
        {
            var listings = await ReadListingsAsync(connectionString, regime.Programme, ct).ConfigureAwait(false);
            foreach (var group in listings.GroupBy(l => l.SourceCode))
            {
                outcomes.Add(await SeedRegimeAsync(regime, group.Key, group.ToList(), by, ct).ConfigureAwait(false));
            }
        }

        return outcomes;
    }

    private async Task<SeedOutcome> SeedRegimeAsync((string Programme, CampaignKind Kind, string TitleSk, string SummarySk) regime, string sourceCode, List<Listing> listings, string by, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var slug = CampaignService.Slugify($"regime-{sourceCode}-{regime.Programme}");
        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Slug == slug, ct).ConfigureAwait(false);
        if (campaign is null)
        {
            campaign = new Campaign
            {
                Id = Guid.CreateVersion7(),
                Slug = slug,
                Title = $"{regime.TitleSk} – zoznam {sourceCode}",
                Summary = regime.SummarySk,
                Kind = regime.Kind,
                Status = CampaignStatus.Active,
                Editorial = EditorialStatus.Published,
                Countries = ["EU"],
                CreatedBy = by,
                CreatedAt = now,
                UpdatedAt = now,
                PublishedAt = now,
            };
            db.Campaigns.Add(campaign);
        }

        var ids = listings.Select(l => l.DesignationId).ToList();
        var actors = await db.Actors.Where(a => a.SanctionDesignationId != null && ids.Contains(a.SanctionDesignationId.Value)).ToDictionaryAsync(a => a.SanctionDesignationId!.Value, ct).ConfigureAwait(false);
        var existingClaims = await db.Claims
            .Where(c => c.CampaignId == campaign.Id && c.Predicate == Predicate.ListedUnder)
            .Select(c => c.SubjectActorId)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var claimed = existingClaims.ToHashSet();

        int actorsNew = 0, claimsNew = 0, stale = 0;
        foreach (var listing in listings)
        {
            if (!actors.TryGetValue(listing.DesignationId, out var actor))
            {
                actor = new Actor
                {
                    Id = Guid.CreateVersion7(),
                    Kind = listing.SubjectType switch
                    {
                        "person" => ActorKind.Person,
                        "organisation" => IsUnit(listing.Name) ? ActorKind.MilitaryUnit : ActorKind.Organisation,
                        _ => ActorKind.Organisation,
                    },
                    Name = listing.Name,
                    NameNormalized = NameNormalizer.Normalize(listing.Name),
                    Country = listing.Country,
                    Description = listing.Function,
                    IsPublicFigure = true,
                    SanctionDesignationId = listing.DesignationId,
                    SanctionSourceCode = sourceCode,
                    SanctionCachedName = listing.Name,
                    LinkVerifiedAt = now,
                    CreatedBy = by,
                    CreatedAt = now,
                    UpdatedAt = now,
                };
                foreach (var alias in listing.Aliases.Distinct(StringComparer.OrdinalIgnoreCase))
                {
                    var normalized = NameNormalizer.Normalize(alias);
                    if (normalized.Length > 0 && normalized != actor.NameNormalized && actor.Aliases.All(x => x.NameNormalized != normalized))
                    {
                        actor.Aliases.Add(new ActorAlias { ActorId = actor.Id, Name = alias, NameNormalized = normalized, Origin = "sanctions", SourceUrl = listing.SourceUrl, CreatedAt = now });
                    }
                }

                db.Actors.Add(actor);
                actors[listing.DesignationId] = actor;
                actorsNew++;
            }
            else
            {
                actor.SanctionCachedName = listing.Name;
                actor.LinkVerifiedAt = now;
            }

            if (!listing.IsActive)
            {
                actor.LinkStaleAt ??= now;
                stale++;
            }

            if (claimed.Contains(actor.Id))
            {
                continue;
            }

            var quote = !string.IsNullOrWhiteSpace(listing.Reason) ? listing.Reason.Trim()
                : !string.IsNullOrWhiteSpace(listing.Remarks) ? listing.Remarks.Trim()
                : $"{listing.Name} — programme {regime.Programme} ({sourceCode})";

            db.Claims.Add(new Claim
            {
                CampaignId = campaign.Id,
                SubjectActorId = actor.Id,
                Predicate = Predicate.ListedUnder,
                Statement = $"Zaradený(á) pod sankčný program {regime.Programme} v zozname {sourceCode}" + (listing.ListedAt is { } at ? $" ({at:d. M. yyyy})" : string.Empty) + ".",
                Level = EvidenceLevel.A,
                Status = ClaimStatus.Confirmed,
                FoundBy = FoundBy.OfficialAct,
                OccurredFrom = listing.ListedAt,
                CreatedBy = by,
                CreatedAt = now,
                UpdatedAt = now,
                DecidedBy = by,
                DecidedAt = now,
                DecisionNote = "Seed zo sankčného zoznamu.",
                Evidence =
                {
                    new Evidence
                    {
                        Url = listing.SourceUrl,
                        Publisher = listing.Publisher,
                        Title = $"{sourceCode}: {listing.Name}",
                        Quote = quote.Length > 4000 ? quote[..4000] : quote,
                        PublishedOn = listing.ListedAt,
                        RetrievedAt = now,
                        AddedBy = by,
                    },
                },
            });
            claimed.Add(actor.Id);
            claimsNew++;
        }

        campaign.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Seeded {Programme}/{Source}: {Listings} listings, {Actors} new actors, {Claims} new claims, {Stale} stale.", regime.Programme, sourceCode, listings.Count, actorsNew, claimsNew, stale);
        return new SeedOutcome(regime.Programme, sourceCode, listings.Count, actorsNew, claimsNew, stale);
    }

    private static bool IsUnit(string name) =>
        name.Contains("unit", StringComparison.OrdinalIgnoreCase)
        || name.Contains("GRU", StringComparison.Ordinal)
        || name.Contains("battalion", StringComparison.OrdinalIgnoreCase)
        || name.Contains("centre", StringComparison.OrdinalIgnoreCase) && name.Contains("warfare", StringComparison.OrdinalIgnoreCase);

    private sealed record Listing(Guid DesignationId, string SourceCode, string Publisher, string SourceUrl, string SubjectType, bool IsActive,
        string Name, List<string> Aliases, string? Country, string? Function, string? Reason, string? Remarks, DateOnly? ListedAt);

    /// <summary>
    /// Every designation under the programme, with its primary name, its aliases and its
    /// first nationality or address country. The EU prints the name in every official
    /// language and marks one of them primary, whichever came first in the file
    /// (Portuguese, for GRU unit 29155); the English one is an alias. So: the English name
    /// whatever its kind, then the primary, then the file order. Some EU entries carry no
    /// primary name at all (Voice of Europe is aliases only), and an alias stands in.
    /// </summary>
    private static async Task<List<Listing>> ReadListingsAsync(string connectionString, string programme, CancellationToken ct)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(ct).ConfigureAwait(false);

        var listings = new Dictionary<Guid, Listing>();
        await using (var command = new NpgsqlCommand(
            """
            SELECT d.id, s.code, s.publisher, s.landing_page_url, d.subject_type, d.status = 'active',
                   d.function, d.reason, d.remarks, d.listed_at,
                   (SELECT n.name_original FROM designation_name n WHERE n.designation_id = d.id AND n.kind IN ('primary', 'alias')
                    ORDER BY (upper(n.language_code) = 'EN') DESC, (n.kind = 'primary') DESC, n.id LIMIT 1),
                   (SELECT a.country_iso2 FROM designation_address a WHERE a.designation_id = d.id AND a.country_iso2 IS NOT NULL ORDER BY a.id LIMIT 1),
                   (SELECT x.country_iso2 FROM designation_nationality x WHERE x.designation_id = d.id AND x.country_iso2 IS NOT NULL ORDER BY x.id LIMIT 1)
            FROM designation d
            JOIN sanctions_source s ON s.id = d.source_id
            JOIN designation_programme dp ON dp.designation_id = d.id
            JOIN sanction_programme p ON p.id = dp.programme_id
            WHERE p.code = @programme AND d.valid_to IS NULL
            """, connection))
        {
            command.Parameters.AddWithValue("programme", programme);
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                var id = reader.GetGuid(0);
                var name = reader.IsDBNull(10) ? null : reader.GetString(10);
                if (name is null || listings.ContainsKey(id))
                {
                    continue;
                }

                listings[id] = new Listing(
                    id, reader.GetString(1), reader.GetString(2), reader.GetString(3), reader.GetString(4), reader.GetBoolean(5),
                    name, [],
                    reader.IsDBNull(12) ? (reader.IsDBNull(11) ? null : reader.GetString(11)) : reader.GetString(12),
                    reader.IsDBNull(6) ? null : reader.GetString(6),
                    reader.IsDBNull(7) ? null : reader.GetString(7),
                    reader.IsDBNull(8) ? null : reader.GetString(8),
                    reader.IsDBNull(9) ? null : DateOnly.FromDateTime(reader.GetDateTime(9)));
            }
        }

        if (listings.Count == 0)
        {
            return [];
        }

        await using (var command = new NpgsqlCommand(
            """
            SELECT n.designation_id, n.name_original
            FROM designation_name n
            WHERE n.designation_id = ANY(@ids) AND n.kind IN ('alias', 'native_script')
            ORDER BY n.id
            """, connection))
        {
            command.Parameters.AddWithValue("ids", listings.Keys.ToArray());
            await using var reader = await command.ExecuteReaderAsync(ct).ConfigureAwait(false);
            while (await reader.ReadAsync(ct).ConfigureAwait(false))
            {
                if (listings.TryGetValue(reader.GetGuid(0), out var listing) && listing.Aliases.Count < 20)
                {
                    listing.Aliases.Add(reader.GetString(1));
                }
            }
        }

        return listings.Values.OrderBy(l => l.SourceCode).ThenBy(l => l.Name).ToList();
    }
}
