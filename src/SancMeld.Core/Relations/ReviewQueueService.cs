using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Search;

namespace SancMeld.Core.Relations;

/// <summary>
/// Every tie between a listing and a party that a person still has to decide, in one
/// list, the most urgent first — so nobody has to open listing after listing to find
/// them. The same ties, statuses and visibility as the graph: a caller who may not see
/// the additional information does not find it here either.
///
/// Urgent is what the graph colours red: a machine's candidate that reaches a binding
/// listing or claims control. A name match in the Slovak register is never urgent by
/// itself — it comes after, ordered so that one whose address is in the listing's
/// country comes first.
/// </summary>
public sealed class ReviewQueueService(SancMeldDbContext db, RelatedPartyQueryService relatedParties)
{
    public const int MaxItems = 500;
    private const decimal ControllingShare = 0.5m;

    /// <summary>
    /// The ties of one status, optionally of one register and of parties in one country
    /// (ISO 3166-1 alpha-2: SK, CZ; null is everywhere), the most urgent first.
    /// </summary>
    public async Task<ReviewQueueView> ListAsync(LinkStatus status = LinkStatus.Candidate, string? system = null, string? country = null, int limit = 200, CancellationToken ct = default)
    {
        var hidden = await relatedParties.HiddenSystemsAsync(ct).ConfigureAwait(false);
        var take = Math.Clamp(limit, 1, MaxItems);

        var visible = db.DesignationRelations.AsNoTracking()
            .Where(r => r.IsCurrent && r.Identity!.ExternalId != null)
            .Where(r => !hidden.Contains(r.Identity!.System) && !hidden.Contains(r.Party!.System));

        // A rejected identity takes every tie through it out of the queue; the tie itself
        // is still listed among the rejected ones.
        visible = status switch
        {
            LinkStatus.Candidate => visible.Where(r => r.Status == LinkStatus.Candidate && r.Identity!.Status != LinkStatus.Rejected),
            LinkStatus.Rejected => visible.Where(r => r.Status == LinkStatus.Rejected || r.Identity!.Status == LinkStatus.Rejected),
            _ => visible.Where(r => r.Status == status && r.Identity!.Status != LinkStatus.Rejected),
        };

        if (country is { Length: 2 })
        {
            var iso = country.ToUpperInvariant();
            visible = visible.Where(r => r.Party!.CountryIso2 == iso);
        }

        var countries = await visible
            .GroupBy(r => r.Party!.CountryIso2 ?? "")
            .Select(g => new { Country = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var counts = await visible
            .GroupBy(r => r.Identity!.System)
            .Select(g => new { System = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(system))
        {
            visible = visible.Where(r => r.Identity!.System == system);
        }

        // Newest first from the database, then ranked here: the rank needs the listing's
        // legal effect, which lives on a revision, not on the tie.
        var rows = await visible
            .Include(r => r.Identity)
            .Include(r => r.Party!).ThenInclude(p => p.Names)
            .OrderByDescending(r => r.LastSeenAt)
            .Take(MaxItems)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var listings = await relatedParties.CurrentListingsAsync(rows.Select(r => r.Identity!), ct).ConfigureAwait(false);

        var items = new List<ReviewItem>();
        foreach (var r in rows)
        {
            if (!listings.TryGetValue((r.Identity!.SourceId, r.Identity.SourceRecordKey), out var listing))
            {
                continue;
            }

            var urgent = !RelationSystems.IsNameOnlyRegister(r.Identity.System)
                && (listing.LegalEffect == LegalEffect.Binding || r.Share >= ControllingShare);

            items.Add(new ReviewItem
            {
                Id = r.Id,
                IdentityId = r.IdentityId,
                System = r.Identity.System,
                Kind = r.Kind,
                Status = r.Status,
                IdentityStatus = r.Identity.Status,
                Priority = r.Status != LinkStatus.Candidate ? ReviewPriority.Decided
                    : urgent ? ReviewPriority.Review
                    : ReviewPriority.Candidate,
                CountryAgrees = r.Note?.Contains("agrees with the listing", StringComparison.Ordinal) == true
                    || r.Note?.Contains("the listing's country", StringComparison.Ordinal) == true,
                Party = RelatedPartyQueryService.ToView(r.Party!),
                Listing = new ReviewListing
                {
                    DesignationId = listing.Id,
                    Name = listing.Names.FirstOrDefault()?.Value ?? listing.SourceRecordKey,
                    LatinName = listing.Names.Select(n => n.Value).FirstOrDefault(n => !n.Any(c => c is >= 'Ѐ' and <= 'ӿ')),
                    Source = listing.Source,
                    LegalEffect = listing.LegalEffect,
                    SubjectType = listing.SubjectType,
                    ListedAt = listing.ListedAt,
                },
                Share = r.Share,
                StartDate = r.StartDate,
                EndDate = r.EndDate,
                Note = r.Note,
                MatchNote = r.Identity.Note,
                MatchedName = r.Identity.MatchedName,
                IdentityLabel = r.Identity.Label,
                IdentityUrl = RelatedPartyQueryService.RecordUrl(r.Identity.System, r.Identity.ExternalId!) is { Length: > 0 } url ? url : null,
                Property = r.Property,
                MatchedLabel = r.Identity.MatchedLabel,
                BirthDateMatched = r.Identity.BirthDateMatched,
                EvidenceUrls = r.EvidenceUrls is null ? [] : JsonSerializer.Deserialize<List<string>>(r.EvidenceUrls) ?? [],
                DecidedBy = r.DecidedBy,
                DecidedAt = r.DecidedAt,
                FirstSeenAt = r.FirstSeenAt,
                LastSeenAt = r.LastSeenAt,
            });
        }

        var ranked = items
            .OrderBy(i => i.Priority)
            .ThenByDescending(i => i.CountryAgrees)
            .ThenByDescending(i => i.EndDate is null)
            .ThenByDescending(i => i.LastSeenAt)
            .ToList();

        return new ReviewQueueView
        {
            Status = status,
            Total = ranked.Count,
            Items = ranked.Take(take).ToList(),
            Systems = counts.OrderByDescending(c => c.Count).ToDictionary(c => c.System, c => c.Count),
            Countries = countries.Where(c => c.Country.Length == 2).OrderByDescending(c => c.Count).ToDictionary(c => c.Country, c => c.Count),
        };
    }
}

/// <summary>Review first (red in the graph), then candidates, then decided ties.</summary>
public enum ReviewPriority { Review, Candidate, Decided }

public sealed record ReviewQueueView
{
    public required LinkStatus Status { get; init; }
    public required int Total { get; init; }
    public required IReadOnlyList<ReviewItem> Items { get; init; }

    /// <summary>How many ties of this status each register holds, before the system filter.</summary>
    public required IReadOnlyDictionary<string, int> Systems { get; init; }

    /// <summary>How many ties of this status lead to parties in each country, after the country filter.</summary>
    public required IReadOnlyDictionary<string, int> Countries { get; init; }
}

public sealed record ReviewListing
{
    public required Guid DesignationId { get; init; }
    public required string Name { get; init; }

    /// <summary>A Latin spelling when the first is Cyrillic: the EU writes Russians in Cyrillic first.</summary>
    public string? LatinName { get; init; }
    public required string Source { get; init; }
    public required LegalEffect LegalEffect { get; init; }
    public required SubjectType SubjectType { get; init; }
    public DateOnly? ListedAt { get; init; }
}

public sealed record ReviewItem
{
    /// <summary>The tie's id: what POST /relations/decisions takes with target ListingRelation.</summary>
    public required long Id { get; init; }
    public required long IdentityId { get; init; }
    public required string System { get; init; }
    public required RelationKind Kind { get; init; }
    public required LinkStatus Status { get; init; }
    public required LinkStatus IdentityStatus { get; init; }
    public required ReviewPriority Priority { get; init; }

    /// <summary>The register's address is in the listing's country: the stronger of two name matches.</summary>
    public bool CountryAgrees { get; init; }
    public required RelatedPartyView Party { get; init; }
    public required ReviewListing Listing { get; init; }
    public decimal? Share { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public string? Note { get; init; }
    public string? MatchNote { get; init; }
    public string? MatchedName { get; init; }
    public string? MatchedLabel { get; init; }

    /// <summary>The register's own record of the listed subject (the Wikidata item of the person), when it has an address.</summary>
    public string? IdentityLabel { get; init; }
    public string? IdentityUrl { get; init; }

    /// <summary>What the register calls the tie: a Wikidata property (P488), a Slovak role (Konateľ).</summary>
    public string? Property { get; init; }
    public bool BirthDateMatched { get; init; }
    public IReadOnlyList<string> EvidenceUrls { get; init; } = [];
    public string? DecidedBy { get; init; }
    public DateTimeOffset? DecidedAt { get; init; }
    public DateTimeOffset FirstSeenAt { get; init; }
    public DateTimeOffset LastSeenAt { get; init; }
}
