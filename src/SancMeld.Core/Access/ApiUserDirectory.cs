using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Data;

namespace SancMeld.Core.Access;

/// <summary>
/// Everyone who signs in, with a request budget.
///
/// Anybody with a verified Google address may screen; what keeps that from being a free
/// screening service for the internet is the budget. A new address gets
/// <see cref="ApiUserDirectory.DefaultLimit"/> requests in total, not per day; raising it
/// is a row update in the database, by whoever runs the instance — there is no screen
/// for it on purpose, so nobody can raise their own.
///
/// Separate from the admin allow list: an administrator manages who administers, a user
/// screens. Being one says nothing about the other.
/// </summary>
public sealed class ApiUserDirectory(SancMeldDbContext db, TimeProvider clock)
{
    /// <summary>Budget of an address seen for the first time, unless the host configures another.</summary>
    public const int DefaultLimit = 50;

    /// <summary>The user behind an address, created with <paramref name="defaultLimit"/> on first sight.</summary>
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

    /// <summary>
    /// Spends one request, atomically: one UPDATE that only succeeds while the budget
    /// lasts, so two requests at the same moment cannot both spend the last one.
    /// </summary>
    public async Task<QuotaState> TryConsumeAsync(string email, CancellationToken ct = default)
    {
        var key = Normalise(email);
        var spent = await db.ApiUsers
            .Where(u => u.Email == key && u.RequestsUsed < u.RequestLimit)
            .ExecuteUpdateAsync(s => s.SetProperty(u => u.RequestsUsed, u => u.RequestsUsed + 1), ct)
            .ConfigureAwait(false);

        var state = await db.ApiUsers.AsNoTracking()
            .Where(u => u.Email == key)
            .Select(u => new { u.RequestLimit, u.RequestsUsed })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        return new QuotaState(spent == 1, state?.RequestLimit ?? 0, state?.RequestsUsed ?? 0);
    }

    /// <summary>
    /// Records a request for a bigger budget, or updates the open one: an address has at
    /// most one request waiting, so resubmitting corrects it instead of queueing another.
    /// </summary>
    public async Task<QuotaRequest> RequestMoreAsync(string email, string reason, string industry, CancellationToken ct = default)
    {
        var key = Normalise(email);
        var now = clock.GetUtcNow();
        var user = await db.ApiUsers.AsNoTracking().FirstOrDefaultAsync(u => u.Email == key, ct).ConfigureAwait(false);

        var request = await db.QuotaRequests.FirstOrDefaultAsync(r => r.Email == key && r.Status == "pending", ct).ConfigureAwait(false);
        if (request is null)
        {
            request = new QuotaRequest { Email = key, Reason = reason, Industry = industry, CreatedAt = now };
            db.QuotaRequests.Add(request);
        }
        else
        {
            request.UpdatedAt = now;
        }

        request.Reason = reason.Trim();
        request.Industry = industry.Trim();
        request.RequestLimitAtRequest = user?.RequestLimit ?? 0;
        request.RequestsUsedAtRequest = user?.RequestsUsed ?? 0;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return request;
    }

    /// <summary>When the open request of this address was sent, if there is one.</summary>
    public async Task<DateTimeOffset?> PendingRequestAtAsync(string email, CancellationToken ct = default)
    {
        var key = Normalise(email);
        return await db.QuotaRequests.AsNoTracking()
            .Where(r => r.Email == key && r.Status == "pending")
            .Select(r => (DateTimeOffset?)(r.UpdatedAt ?? r.CreatedAt))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>Whether the address may see the additional information. False for an address never seen.</summary>
    public async Task<bool> IsAllowedForAdditionalInfoAsync(string email, CancellationToken ct = default)
    {
        var key = Normalise(email);
        return await db.ApiUsers.AsNoTracking()
            .AnyAsync(u => u.Email == key && u.IsAllowedForAdditionalInfo, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Grants or withdraws the additional information. An address that has not signed in
    /// yet gets its row now, with the default budget, so the grant is waiting for it.
    /// </summary>
    public async Task<ApiUser> SetAdditionalInfoAsync(string email, bool allowed, int defaultLimit, CancellationToken ct = default)
    {
        var key = Normalise(email);
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Email == key, ct).ConfigureAwait(false);

        if (user is null)
        {
            user = new ApiUser { Id = Guid.CreateVersion7(), Email = key, RequestLimit = defaultLimit, CreatedAt = clock.GetUtcNow() };
            db.ApiUsers.Add(user);
        }

        user.IsAllowedForAdditionalInfo = allowed;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    /// <summary>The addresses allowed the additional information, for the operator's listing.</summary>
    public async Task<IReadOnlyList<string>> AllowedForAdditionalInfoAsync(CancellationToken ct = default) =>
        await db.ApiUsers.AsNoTracking()
            .Where(u => u.IsAllowedForAdditionalInfo)
            .OrderBy(u => u.Email)
            .Select(u => u.Email)
            .ToListAsync(ct)
            .ConfigureAwait(false);

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}

/// <summary>Whether the request was allowed, and the budget after it.</summary>
public sealed record QuotaState(bool Allowed, int Limit, int Used)
{
    public int Remaining => Math.Max(0, Limit - Used);
}
