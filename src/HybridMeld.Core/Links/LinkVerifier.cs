using HybridMeld.Core.Configuration;
using HybridMeld.Core.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HybridMeld.Core.Links;

public sealed record VerifyOutcome(int Checked, int Refreshed, int Stale, int Skipped);

/// <summary>
/// Re-reads every link to a sanctions record or a PEP person that has not been checked
/// for a week: refreshes the cached name, marks the link stale when the target is gone or
/// delisted. The link stays; the screen says "the record is no longer active" instead of
/// losing a tie an analyst set by hand.
/// </summary>
public sealed class LinkVerifier(HybridMeldDbContext db, LinkResolver links, IOptions<HybridMeldOptions> options, TimeProvider clock, ILogger<LinkVerifier> logger)
{
    public async Task<VerifyOutcome> VerifyAsync(int limit, bool all, CancellationToken ct)
    {
        var now = clock.GetUtcNow();
        var cutoff = now.AddHours(-options.Value.Links.VerifyAfterHours);
        var query = db.Actors.Where(a => a.SanctionDesignationId != null || a.PepPersonId != null);
        if (!all)
        {
            query = query.Where(a => a.LinkVerifiedAt == null || a.LinkVerifiedAt < cutoff);
        }

        var actors = await query.OrderBy(a => a.LinkVerifiedAt).Take(Math.Clamp(limit, 1, 5000)).ToListAsync(ct).ConfigureAwait(false);
        int refreshed = 0, stale = 0, skipped = 0;

        foreach (var actor in actors)
        {
            var isStale = false;
            var touched = false;

            if (actor.SanctionDesignationId is { } designationId)
            {
                if (!links.SanctionsConfigured)
                {
                    skipped++;
                }
                else
                {
                    var target = await links.FindDesignationAsync(designationId, ct).ConfigureAwait(false);
                    touched = true;
                    if (target is null || !target.IsCurrent)
                    {
                        isStale = true;
                    }

                    if (target is not null)
                    {
                        actor.SanctionSourceCode = target.SourceCode;
                        actor.SanctionCachedName = target.Name;
                    }
                }
            }

            if (actor.PepPersonId is { } personId)
            {
                if (!links.PepConfigured)
                {
                    skipped++;
                }
                else
                {
                    var target = await links.FindPepPersonAsync(personId, ct).ConfigureAwait(false);
                    touched = true;
                    if (target is null)
                    {
                        isStale = true;
                    }
                    else
                    {
                        actor.PepSourceCode = target.SourceCode;
                        actor.PepCachedName = target.Name;
                    }
                }
            }

            if (!touched)
            {
                continue;
            }

            actor.LinkVerifiedAt = now;
            if (isStale)
            {
                actor.LinkStaleAt ??= now;
                stale++;
            }
            else
            {
                actor.LinkStaleAt = null;
                refreshed++;
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Links verified: {Checked} checked, {Refreshed} fresh, {Stale} stale, {Skipped} skipped.", actors.Count, refreshed, stale, skipped);
        return new VerifyOutcome(actors.Count, refreshed, stale, skipped);
    }
}
