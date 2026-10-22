using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Data;

namespace PepMeld.Core.Access;

/// <summary>
/// Everyone who has signed in, as seen here: the address, the name the provider gave
/// and when they were last seen. Nothing about what they may do or how much: the grant,
/// the block and the budget are SancMeld's account (PepAccess), one for both services,
/// so that a check and a screening count against the same number on one screen.
/// </summary>
public sealed class ApiUserDirectory(PepMeldDbContext db, TimeProvider clock)
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

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}

/// <summary>Whether the request was allowed, and the budget after it.</summary>
public sealed record QuotaState(bool Allowed, int Limit, int Used)
{
    /// <summary>No accounts database, no budget: a development host.</summary>
    public static QuotaState Unlimited { get; } = new(true, int.MaxValue, 0);

    public int Remaining => Math.Max(0, Limit - Used);
}
