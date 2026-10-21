using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Workspace;

/// <summary>The "current threats" screen: cases and claims that changed since a moment, newest first.</summary>
public sealed class FeedService(HybridMeldDbContext db, TimeProvider clock)
{
    public async Task<FeedView> SinceAsync(DateTimeOffset? since, int limit, CancellationToken ct)
    {
        var from = since ?? clock.GetUtcNow().AddDays(-30);
        var take = Math.Clamp(limit, 1, 200);

        var campaigns = await db.Campaigns.AsNoTracking()
            .Include(c => c.AttributedTo)
            .Include(c => c.Claims).ThenInclude(x => x.Subject)
            .Include(c => c.Claims).ThenInclude(x => x.Object)
            .Where(c => c.Editorial == EditorialStatus.Published && c.UpdatedAt >= from)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(take)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var claims = await db.Claims.AsNoTracking()
            .Include(c => c.Campaign)
            .Include(c => c.Subject)
            .Include(c => c.Object)
            .Include(c => c.Evidence)
            .Where(c => c.Campaign!.Editorial == EditorialStatus.Published && c.Status != ClaimStatus.Withdrawn && c.Status != ClaimStatus.Rejected && c.UpdatedAt >= from)
            .OrderByDescending(c => c.UpdatedAt)
            .Take(take)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new FeedView(from, campaigns.Select(Mapping.ToSummary).ToList(), claims.Select(Mapping.ToView).ToList());
    }
}
