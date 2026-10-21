using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Workspace;

/// <summary>The "current threats" screen: cases and claims that changed since a moment, newest first.</summary>
public sealed class FeedService(HybridMeldDbContext db, TimeProvider clock)
{
    /// <summary>The same filter bar as the list of cases; without a period, the last 30 days.</summary>
    public async Task<FeedView> SinceAsync(CampaignFilter filter, int limit, CancellationToken ct)
    {
        var from = filter.Since ?? clock.GetUtcNow().AddDays(-30);
        var f = filter with { Since = from };
        var take = Math.Clamp(limit, 1, 200);

        var campaigns = (await CampaignFilters.Apply(db.Campaigns.AsNoTracking()
                .Include(c => c.AttributedTo)
                .Include(c => c.Claims).ThenInclude(x => x.Subject)
                .Include(c => c.Claims).ThenInclude(x => x.Object), f)
            .OrderByDescending(c => c.UpdatedAt)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false))
            .Where(c => CampaignFilters.Matches(c, f))
            .ToList();

        // The claims of the cases the filter lets through, filtered once more as claims:
        // a case with one confirmed A-level claim passes, its D-level candidates do not.
        var campaignIds = campaigns.Select(c => c.Id).ToList();
        var claims = await db.Claims.AsNoTracking()
            .Include(c => c.Campaign)
            .Include(c => c.Subject)
            .Include(c => c.Object)
            .Include(c => c.Evidence)
            .Where(c => campaignIds.Contains(c.CampaignId) && c.Status != ClaimStatus.Withdrawn && c.Status != ClaimStatus.Rejected && c.UpdatedAt >= from)
            .OrderByDescending(c => c.UpdatedAt)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new FeedView(
            from,
            campaigns.Take(take).Select(Mapping.ToSummary).ToList(),
            claims.Where(x => CampaignFilters.ClaimMatches(x, f)).Take(take).Select(Mapping.ToView).ToList());
    }
}
