using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Data;

namespace SancMeld.Core.Access;

/// <summary>
/// API keys: how a program — an agent, a script, a back office — calls the API without a
/// browser to sign in with. A key acts as the user it belongs to and spends that user's
/// request budget; it can screen, never manage the allow list.
///
/// Only a SHA-256 of the key is stored. The key is shown once, when it is made; a lost
/// key is revoked and replaced, not recovered. A key has 256 random bits, so a fast hash
/// is enough — there is nothing to guess that a slow one would protect.
/// </summary>
public sealed class ApiKeyDirectory(SancMeldDbContext db, TimeProvider clock)
{
    /// <summary>Every key starts with this, so one pasted into a log or a chat is recognisable.</summary>
    public const string Prefix = "smk_";

    /// <summary>Makes a key for <paramref name="email"/>; the plain key is in the result and nowhere else.</summary>
    public async Task<(string Key, ApiKey Record)> CreateAsync(string email, string name, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(email);
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        var key = Prefix + Base64Url(RandomNumberGenerator.GetBytes(32));
        var record = new ApiKey
        {
            Id = Guid.CreateVersion7(),
            Name = name.Trim(),
            OwnerEmail = email.Trim().ToLowerInvariant(),
            KeyHash = Hash(key),
            DisplayPrefix = key[..(Prefix.Length + 6)],
            CreatedAt = clock.GetUtcNow(),
        };

        db.ApiKeys.Add(record);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (key, record);
    }

    /// <summary>The live key behind <paramref name="key"/>, or null; records its use at most once a minute.</summary>
    public async Task<ApiKey?> FindAsync(string key, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return null;
        }

        var hash = Hash(key.Trim());
        var record = await db.ApiKeys.FirstOrDefaultAsync(k => k.KeyHash == hash && k.RevokedAt == null, ct).ConfigureAwait(false);
        if (record is null)
        {
            return null;
        }

        var now = clock.GetUtcNow();
        if (record.LastUsedAt is null || now - record.LastUsedAt > TimeSpan.FromMinutes(1))
        {
            record.LastUsedAt = now;
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
        }

        return record;
    }

    public async Task<IReadOnlyList<ApiKey>> ListAsync(CancellationToken ct = default) =>
        await db.ApiKeys.AsNoTracking().OrderBy(k => k.CreatedAt).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Revokes the keys whose shown prefix is <paramref name="displayPrefix"/>; returns how many.</summary>
    public async Task<int> RevokeAsync(string displayPrefix, CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        return await db.ApiKeys
            .Where(k => k.DisplayPrefix == displayPrefix && k.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), ct)
            .ConfigureAwait(false);
    }

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
