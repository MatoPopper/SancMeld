using System.Security.Cryptography;
using System.Text;
using HybridMeld.Core.Data;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Access;

/// <summary>
/// API keys for programs, the shape PepMeld uses: a key acts as its owner, only a
/// SHA-256 of it is stored, it is shown once. The prefix hmk_ keeps one pasted into a
/// log distinguishable from SancMeld's smk_ and PepMeld's pmk_. No key is issued today:
/// the agent is not connected (user's decision, 2026-10-21); the mechanism waits.
/// </summary>
public sealed class ApiKeyDirectory(HybridMeldDbContext db, TimeProvider clock)
{
    public const string Prefix = "hmk_";

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
