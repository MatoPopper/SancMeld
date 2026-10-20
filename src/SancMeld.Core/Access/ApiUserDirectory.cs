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
            .AnyAsync(u => u.Email == key && u.IsAllowedForAdditionalInfo && !u.IsBlocked, ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Grants or withdraws the additional information. An address that has not signed in
    /// yet gets its row now, with the default budget, so the grant is waiting for it.
    /// </summary>
    public async Task<ApiUser> SetAdditionalInfoAsync(string email, bool allowed, int defaultLimit, CancellationToken ct = default, string changedBy = "cli")
    {
        var key = Normalise(email);
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Email == key, ct).ConfigureAwait(false);

        if (user is null)
        {
            user = new ApiUser { Id = Guid.CreateVersion7(), Email = key, RequestLimit = defaultLimit, CreatedAt = clock.GetUtcNow() };
            db.ApiUsers.Add(user);
        }

        user.IsAllowedForAdditionalInfo = allowed;
        user.AdditionalInfoChangedBy = changedBy;
        user.AdditionalInfoChangedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    /// <summary>Sets one of the administrator flags. CLI only: nobody grants these from a screen.</summary>
    public async Task<ApiUser> SetRoleAsync(string email, UserRole role, bool on, int defaultLimit, CancellationToken ct = default)
    {
        var key = Normalise(email);
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Email == key, ct).ConfigureAwait(false);

        if (user is null)
        {
            user = new ApiUser { Id = Guid.CreateVersion7(), Email = key, RequestLimit = defaultLimit, CreatedAt = clock.GetUtcNow() };
            db.ApiUsers.Add(user);
        }

        switch (role)
        {
            case UserRole.UserAdmin: user.IsUserAdmin = on; break;
            case UserRole.SystemAdmin: user.IsSystemAdmin = on; break;
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    /// <summary>Whether the address may manage users: a user administrator who is not blocked.</summary>
    public async Task<bool> IsUserAdminAsync(string email, CancellationToken ct = default)
    {
        var key = Normalise(email);
        return await db.ApiUsers.AsNoTracking()
            .AnyAsync(u => u.Email == key && u.IsUserAdmin && !u.IsBlocked, ct)
            .ConfigureAwait(false);
    }

    /// <summary>Whether the address is blocked. False for an address never seen.</summary>
    public async Task<bool> IsBlockedAsync(string email, CancellationToken ct = default)
    {
        var key = Normalise(email);
        return await db.ApiUsers.AsNoTracking().AnyAsync(u => u.Email == key && u.IsBlocked, ct).ConfigureAwait(false);
    }

    /// <summary>Everyone who has signed in or been granted something, most recently seen first.</summary>
    public async Task<IReadOnlyList<UserView>> ListAsync(CancellationToken ct = default)
    {
        var allowList = await db.Admins.AsNoTracking().Where(a => a.IsActive).Select(a => a.Email).ToListAsync(ct).ConfigureAwait(false);

        var users = await db.ApiUsers.AsNoTracking()
            .OrderByDescending(u => u.LastSeenAt ?? u.CreatedAt)
            .Select(u => new
            {
                User = u,
                Keys = db.ApiKeys.Count(k => k.OwnerEmail == u.Email && k.RevokedAt == null),
                PendingQuota = db.QuotaRequests.Any(r => r.Email == u.Email && r.Status == "pending"),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return [.. users.Select(x => ToView(x.User, x.Keys, x.PendingQuota, allowList.Contains(x.User.Email)))];
    }

    /// <summary>
    /// Blocks or unblocks a user. An administrator — of users, of the system, or on the
    /// admin allow list — is never blocked from here: one administrator locking out the
    /// others is not something a screen should allow, and the database is one step away
    /// for whoever runs the instance.
    /// </summary>
    public async Task<(UserChangeOutcome Outcome, UserView? User)> SetBlockedAsync(Guid id, bool blocked, string by, CancellationToken ct = default)
    {
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return (UserChangeOutcome.NotFound, null);
        }

        var onAllowList = await db.Admins.AnyAsync(a => a.Email == user.Email && a.IsActive, ct).ConfigureAwait(false);
        if (blocked && (user.IsUserAdmin || user.IsSystemAdmin || onAllowList))
        {
            return (UserChangeOutcome.AdminCannotBeBlocked, ToView(user, 0, false, onAllowList));
        }

        if (user.IsBlocked != blocked)
        {
            user.IsBlocked = blocked;
            user.BlockedBy = blocked ? by : null;
            user.BlockedAt = blocked ? clock.GetUtcNow() : null;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return (UserChangeOutcome.Changed, ToView(user, 0, false, onAllowList));
    }

    /// <summary>Grants or withdraws the additional information, from the users screen.</summary>
    public async Task<(UserChangeOutcome Outcome, UserView? User)> SetAdditionalInfoAsync(Guid id, bool allowed, string by, CancellationToken ct = default)
    {
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return (UserChangeOutcome.NotFound, null);
        }

        if (user.IsAllowedForAdditionalInfo != allowed)
        {
            user.IsAllowedForAdditionalInfo = allowed;
            user.AdditionalInfoChangedBy = by;
            user.AdditionalInfoChangedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var onAllowList = await db.Admins.AnyAsync(a => a.Email == user.Email && a.IsActive, ct).ConfigureAwait(false);
        return (UserChangeOutcome.Changed, ToView(user, 0, false, onAllowList));
    }

    /// <summary>Grants or withdraws PEP screening, from the users screen.</summary>
    public async Task<(UserChangeOutcome Outcome, UserView? User)> SetPepAsync(Guid id, bool allowed, string by, CancellationToken ct = default)
    {
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Id == id, ct).ConfigureAwait(false);
        if (user is null)
        {
            return (UserChangeOutcome.NotFound, null);
        }

        if (user.IsAllowedForPep != allowed)
        {
            user.IsAllowedForPep = allowed;
            user.PepChangedBy = by;
            user.PepChangedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        var onAllowList = await db.Admins.AnyAsync(a => a.Email == user.Email && a.IsActive, ct).ConfigureAwait(false);
        return (UserChangeOutcome.Changed, ToView(user, 0, false, onAllowList));
    }

    /// <summary>
    /// Grants or withdraws PEP screening by address, from the CLI. An address that has not
    /// signed in yet gets its row now, so the grant is waiting for it.
    /// </summary>
    public async Task<ApiUser> SetPepAsync(string email, bool allowed, int defaultLimit, CancellationToken ct = default, string changedBy = "cli")
    {
        var key = Normalise(email);
        var user = await db.ApiUsers.FirstOrDefaultAsync(u => u.Email == key, ct).ConfigureAwait(false);

        if (user is null)
        {
            user = new ApiUser { Id = Guid.CreateVersion7(), Email = key, RequestLimit = defaultLimit, CreatedAt = clock.GetUtcNow() };
            db.ApiUsers.Add(user);
        }

        user.IsAllowedForPep = allowed;
        user.PepChangedBy = changedBy;
        user.PepChangedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return user;
    }

    /// <summary>Whether the address may screen for PEPs. False for an address never seen or blocked.</summary>
    public async Task<bool> IsAllowedForPepAsync(string email, CancellationToken ct = default)
    {
        var key = Normalise(email);
        return await db.ApiUsers.AsNoTracking()
            .AnyAsync(u => u.Email == key && u.IsAllowedForPep && !u.IsBlocked, ct)
            .ConfigureAwait(false);
    }

    private static UserView ToView(ApiUser u, int keys, bool pendingQuota, bool onAllowList) => new()
    {
        Id = u.Id,
        Email = u.Email,
        DisplayName = u.DisplayName,
        CreatedAt = u.CreatedAt,
        LastSeenAt = u.LastSeenAt,
        RequestLimit = u.RequestLimit,
        RequestsUsed = u.RequestsUsed,
        ActiveApiKeys = keys,
        PendingQuotaRequest = pendingQuota,
        IsAllowedForAdditionalInfo = u.IsAllowedForAdditionalInfo,
        AdditionalInfoChangedBy = u.AdditionalInfoChangedBy,
        AdditionalInfoChangedAt = u.AdditionalInfoChangedAt,
        IsAllowedForPep = u.IsAllowedForPep,
        PepChangedBy = u.PepChangedBy,
        PepChangedAt = u.PepChangedAt,
        IsUserAdmin = u.IsUserAdmin,
        IsSystemAdmin = u.IsSystemAdmin,
        IsOnAdminAllowList = onAllowList,
        IsBlocked = u.IsBlocked,
        BlockedBy = u.BlockedBy,
        BlockedAt = u.BlockedAt,
    };

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

/// <summary>The administrator flags on a user.</summary>
public enum UserRole { UserAdmin, SystemAdmin }

public enum UserChangeOutcome { Changed, NotFound, AdminCannotBeBlocked }

/// <summary>A user as the users screen shows them.</summary>
public sealed record UserView
{
    public required Guid Id { get; init; }
    public required string Email { get; init; }
    public string? DisplayName { get; init; }
    public DateTimeOffset CreatedAt { get; init; }
    public DateTimeOffset? LastSeenAt { get; init; }
    public int RequestLimit { get; init; }

    /// <summary>Requests made so far, in total: checks, screenings and register lookups.</summary>
    public int RequestsUsed { get; init; }
    public int ActiveApiKeys { get; init; }
    public bool PendingQuotaRequest { get; init; }
    public bool IsAllowedForAdditionalInfo { get; init; }
    public string? AdditionalInfoChangedBy { get; init; }
    public DateTimeOffset? AdditionalInfoChangedAt { get; init; }

    /// <summary>May screen for politically exposed persons in PepMeld.</summary>
    public bool IsAllowedForPep { get; init; }
    public string? PepChangedBy { get; init; }
    public DateTimeOffset? PepChangedAt { get; init; }
    public bool IsUserAdmin { get; init; }
    public bool IsSystemAdmin { get; init; }
    public bool IsOnAdminAllowList { get; init; }

    /// <summary>An administrator of any kind; the screen offers no block button for them.</summary>
    public bool IsAdministrator => IsUserAdmin || IsSystemAdmin || IsOnAdminAllowList;
    public bool IsBlocked { get; init; }
    public string? BlockedBy { get; init; }
    public DateTimeOffset? BlockedAt { get; init; }
}

/// <summary>Whether the request was allowed, and the budget after it.</summary>
public sealed record QuotaState(bool Allowed, int Limit, int Used)
{
    public int Remaining => Math.Max(0, Limit - Used);
}
