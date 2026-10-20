using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using PepMeld.Core.Data;

namespace PepMeld.Core.Access;

/// <summary>
/// API keys for programs. A key acts as the user it belongs to and spends that user's
/// budget. Only a SHA-256 of the key is stored; the key is shown once, when made, and a
/// lost one is revoked and replaced. 256 random bits need no slow hash.
/// </summary>
public sealed class ApiKeyDirectory(PepMeldDbContext db, TimeProvider clock)
{
    /// <summary>Every key starts with this, so one pasted into a log is recognisable, and distinguishable from SancMeld's smk_.</summary>
    public const string Prefix = "pmk_";

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
        await db.ApiKeys.AsNoTracking().OrderBy(k => k.OwnerEmail).ThenBy(k => k.CreatedAt).ToListAsync(ct).ConfigureAwait(false);

    /// <summary>Revokes every live key starting with <paramref name="prefix"/>; returns how many.</summary>
    public async Task<int> RevokeAsync(string prefix, CancellationToken ct = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        var now = clock.GetUtcNow();
        var wanted = prefix.Trim();
        return await db.ApiKeys
            .Where(k => k.RevokedAt == null && k.DisplayPrefix.StartsWith(wanted))
            .ExecuteUpdateAsync(s => s.SetProperty(k => k.RevokedAt, now), ct)
            .ConfigureAwait(false);
    }

    private static string Hash(string key) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(key)));

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}
