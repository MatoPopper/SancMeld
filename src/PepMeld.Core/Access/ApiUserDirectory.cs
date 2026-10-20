using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Data;

namespace PepMeld.Core.Access;

/// <summary>
/// Everyone who may run a check, with a request budget. The shape and the rules are
/// SancMeld's (ApiUserDirectory there): a new address gets a budget in total, not per
/// day, and it is raised by hand. Separate rows in a separate database for now; the day
/// the two services share an account this is the class that goes.
/// </summary>
public sealed class ApiUserDirectory(PepMeldDbContext db, TimeProvider clock)
{
    public const int DefaultLimit = 50;

    public async Task<ApiUser> EnsureAsync(string email, string? name, string? subject, int defaultLimit, CancellationToken ct = default)
    {
        var key = Normalise(email);
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Email == key, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();

        if (user is null)
        {
            user = new ApiUser { Id = Guid.CreateVersion7(), Email = key, RequestLimit = defaultLimit, CreatedAt = now };
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

    /// <summary>Spends one request atomically: one UPDATE that only succeeds while the budget lasts.</summary>
    public async Task<QuotaState> TryConsumeAsync(string email, CancellationToken ct = default)
    {
        var key = Normalise(email);
        var spent = await db.ApiUsers
            .Where(u => u.Email == key && u.RequestsUsed < u.RequestLimit && !u.IsBlocked)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RequestsUsed, u => u.RequestsUsed + 1), ct)
            .ConfigureAwait(false);

        var state = await db.ApiUsers.AsNoTracking()
            .Where(u => u.Email == key)
            .Select(u => new { u.RequestLimit, u.RequestsUsed })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new QuotaState(spent == 1, state?.RequestLimit ?? 0, state?.RequestsUsed ?? 0);
    }

    public async Task<IReadOnlyList<ApiUser>> ListAsync(CancellationToken ct = default) =>
        await db.ApiUsers.AsNoTracking().OrderByDescending(u => u.LastSeenAt ?? u.CreatedAt).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Sets the budget of an address, creating the row if it has never signed in.</summary>
    public async Task<ApiUser> SetLimitAsync(string email, int limit, CancellationToken ct = default)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(limit);
        var user = await EnsureAsync(email, null, null, limit, ct).ConfigureAwait(false);
        user.RequestLimit = limit;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    public async Task<ApiUser> SetBlockedAsync(string email, bool blocked, string by, CancellationToken ct = default)
    {
        var user = await EnsureAsync(email, null, null, DefaultLimit, ct).ConfigureAwait(false);
        user.IsBlocked = blocked;
        user.BlockedBy = blocked ? by : null;
        user.BlockedAt = blocked ? clock.GetUtcNow() : null;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}

/// <summary>Whether the request was allowed, and the budget after it.</summary>
public sealed record QuotaState(bool Allowed, int Limit, int Used)
{
    public int Remaining => Math.Max(0, Limit - Used);
}
