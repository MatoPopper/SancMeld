using System.ComponentModel.DataAnnotations;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SancMeld.Core.Data;

namespace SancMeld.Core.Access;

/// <summary>
/// The allow list of Google accounts that may administer this instance.
///
/// Authentication and authorisation are kept apart on purpose. Google will happily
/// prove that the caller owns some address; that is a fact about the world and says
/// nothing about this deployment. This is the part that says whether that address is
/// anyone here, and it lives in the database rather than in configuration so that
/// adding a colleague does not mean editing a file and restarting the API.
///
/// Every grant and revocation is logged. The table holds the current state; the log is
/// what answers "who could get in last March", and both are needed because a compliance
/// tool where access changes leave no trace is one nobody can audit.
/// </summary>
public sealed class AdminDirectory(SancMeldDbContext db, TimeProvider clock, ILogger<AdminDirectory> logger)
{
    /// <summary>
    /// How stale <see cref="Admin.LastSeenAt"/> is allowed to get before a request
    /// refreshes it. Every authorised call would otherwise write a row, which turns a
    /// read-only listing into a write and gives the table a hot spot for no benefit —
    /// the field only ever gets read by a human deciding whether an entry is still used.
    /// </summary>
    private static readonly TimeSpan SignInWriteInterval = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Addresses are compared lower-cased and trimmed, and stored that way too.
    ///
    /// Google returns the address in whatever case the account was created with, so a
    /// grant typed as "Mato@Example.com" has to match a token carrying
    /// "mato@example.com". Note this deliberately does NOT strip dots or +tags: Gmail
    /// treats those as the same mailbox, most other providers do not, and guessing
    /// wrong here means letting in an address nobody granted.
    /// </summary>
    public static string Normalize(string email) =>
        (email ?? string.Empty).Trim().ToLowerInvariant();

    /// <summary>The entry for this address if it exists and has not been revoked.</summary>
    public async Task<Admin?> FindActiveAsync(string email, CancellationToken ct = default)
    {
        var normalized = Normalize(email);

        if (normalized.Length == 0)
        {
            return null;
        }

        return await db.Admins
            .AsNoTracking()
            .FirstOrDefaultAsync(a => a.Email == normalized && a.IsActive, ct)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<AdminView>> ListAsync(bool includeRevoked = false, CancellationToken ct = default)
    {
        var query = db.Admins.AsNoTracking();

        if (!includeRevoked)
        {
            query = query.Where(a => a.IsActive);
        }

        return await query
            .OrderBy(a => a.Email)
            .Select(a => new AdminView(
                a.Id, a.Email, a.DisplayName, a.IsActive, a.Note,
                a.CreatedAt, a.CreatedBy, a.RevokedAt, a.RevokedBy, a.LastSeenAt))
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Adds an address to the allow list, or restores one that was revoked.
    ///
    /// Nothing is verified about the account beyond the shape of the address — Google
    /// decides whether it exists, and an entry for an address nobody ever signs in with
    /// simply never matches anything.
    /// </summary>
    /// <exception cref="ArgumentException">The address is not one Google could ever assert.</exception>
    public async Task<AdminView> GrantAsync(
        string email,
        string? displayName,
        string? note,
        string grantedBy,
        CancellationToken ct = default)
    {
        var normalized = Normalize(email);

        if (!new EmailAddressAttribute().IsValid(normalized))
        {
            // No parameter name: this message is shown to whoever typed the address, both
            // at the command line and in the API's 400, and "(Parameter 'email')" tacked
            // onto the end of it helps neither of them.
            throw new ArgumentException(
                $"'{email}' is not an email address. The allow list is matched against the address in the "
                + "Google token, so anything else can never match.");
        }

        var now = clock.GetUtcNow();
        var existing = await db.Admins.FirstOrDefaultAsync(a => a.Email == normalized, ct).ConfigureAwait(false);

        if (existing is null)
        {
            existing = new Admin
            {
                Email = normalized,
                DisplayName = displayName,
                Note = note,
                IsActive = true,
                CreatedAt = now,
                CreatedBy = grantedBy,
            };

            db.Admins.Add(existing);
            logger.LogInformation("Admin access granted to {Email} by {GrantedBy}.", normalized, grantedBy);
        }
        else
        {
            var wasRevoked = !existing.IsActive;

            existing.DisplayName = displayName ?? existing.DisplayName;
            existing.Note = note ?? existing.Note;
            existing.IsActive = true;

            // Cleared so the row and the check constraint keep saying the same thing.
            // The revocation itself is in the log, which is where a question about the
            // past belongs anyway.
            existing.RevokedAt = null;
            existing.RevokedBy = null;

            if (wasRevoked)
            {
                existing.CreatedAt = now;
                existing.CreatedBy = grantedBy;
                logger.LogInformation("Admin access restored for {Email} by {GrantedBy}.", normalized, grantedBy);
            }
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return new AdminView(
            existing.Id, existing.Email, existing.DisplayName, existing.IsActive, existing.Note,
            existing.CreatedAt, existing.CreatedBy, existing.RevokedAt, existing.RevokedBy, existing.LastSeenAt);
    }

    /// <summary>Withdraws access without deleting the entry.</summary>
    public async Task<RevokeOutcome> RevokeAsync(Guid id, string revokedBy, CancellationToken ct = default)
    {
        var admin = await db.Admins.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);

        if (admin is null)
        {
            return RevokeOutcome.NotFound;
        }

        if (!admin.IsActive)
        {
            return RevokeOutcome.AlreadyRevoked;
        }

        // Checked here rather than in the endpoint because it is an invariant of the list
        // itself: whoever is calling, an empty allow list means the admin API can only be
        // reopened from a shell on the server.
        var activeCount = await db.Admins.CountAsync(a => a.IsActive, ct).ConfigureAwait(false);

        if (activeCount <= 1)
        {
            return RevokeOutcome.LastActiveAdmin;
        }

        admin.IsActive = false;
        admin.RevokedAt = clock.GetUtcNow();
        admin.RevokedBy = revokedBy;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        logger.LogInformation("Admin access revoked for {Email} by {RevokedBy}.", admin.Email, revokedBy);

        return RevokeOutcome.Revoked;
    }

    /// <summary>
    /// Notes that this entry was just used, and records Google's subject id the first
    /// time it is seen. One statement, no read-modify-write, and skipped entirely if the
    /// row was already touched within the last minute.
    /// </summary>
    public async Task RecordSignInAsync(Guid id, string? googleSubject, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var cutoff = now - SignInWriteInterval;

        await db.Admins
            .Where(a => a.Id == id && (a.LastSeenAt == null || a.LastSeenAt < cutoff || a.GoogleSubject == null))
            .ExecuteUpdateAsync(
                set => set
                    .SetProperty(a => a.LastSeenAt, now)
                    .SetProperty(a => a.GoogleSubject, a => a.GoogleSubject ?? googleSubject),
                ct)
            .ConfigureAwait(false);
    }
}
