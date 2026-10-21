using HybridMeld.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Access;

/// <summary>
/// Everyone who has signed in here, so a row with a name exists to write CreatedBy
/// against and to block. No request budget: the workspace is a desk for a few people,
/// not a metered API. The grant itself is SancMeld's flag, read by HybridAccess.
/// </summary>
public sealed class ApiUserDirectory(HybridMeldDbContext db, TimeProvider clock)
{
    public async Task<ApiUser> EnsureAsync(string email, string? name, string? subject, CancellationToken ct = default)
    {
        var key = Normalise(email);
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Email == key, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        if (user is null)
        {
            user = new ApiUser { Id = Guid.CreateVersion7(), Email = key, CreatedAt = now };
            db.ApiUsers.Add(user);

            try
            {
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (DbUpdateException)
            {
                // Two first requests at once; the other one created the row.
                db.ChangeTracker.Clear();
                user = await db.ApiUsers.FirstAsync(u => u.Email == key, ct).ConfigureAwait(false);
            }
        }

        if (user.DisplayName != name || user.GoogleSubject != subject || user.LastSeenAt is null || now - user.LastSeenAt > TimeSpan.FromMinutes(1))
        {
            user.DisplayName = name ?? user.DisplayName;
            user.GoogleSubject = subject ?? user.GoogleSubject;
            user.LastSeenAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return user;
    }

    public async Task<IReadOnlyList<ApiUser>> ListAsync(CancellationToken ct = default) =>
        await db.ApiUsers.AsNoTracking().OrderByDescending(u => u.LastSeenAt ?? u.CreatedAt).ToListAsync(ct).ConfigureAwait(false);

    public async Task<ApiUser> SetBlockedAsync(string email, bool blocked, string by, CancellationToken ct = default)
    {
        var user = await EnsureAsync(email, null, null, ct).ConfigureAwait(false);
        user.IsBlocked = blocked;
        user.BlockedBy = blocked ? by : null;
        user.BlockedAt = blocked ? clock.GetUtcNow() : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}
