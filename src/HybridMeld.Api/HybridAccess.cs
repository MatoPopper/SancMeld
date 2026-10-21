using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace HybridMeld.Api;

/// <summary>
/// Whether an address may open the workspace, answered by the account it signs in with:
/// the SancMeld user, whose flag is_allowed_for_hybrid is set on SancMeld's users screen
/// or with "users hybrid &lt;email&gt; on". One account, one grant, read where the users
/// are managed, the way PepMeld reads its own flag (PepAccess there).
///
/// Configured with ConnectionStrings:SancMeldAccounts. Without it every verified address
/// may use the workspace, which is what a development host with no SancMeld beside it
/// needs; a production host sets it.
/// </summary>
public sealed class HybridAccess(IConfiguration configuration, TimeProvider clock, ILogger<HybridAccess> logger)
{
    public const string ConnectionStringName = "SancMeldAccounts";

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private readonly string? connectionString = configuration.GetConnectionString(ConnectionStringName);
    private readonly ConcurrentDictionary<string, (bool Allowed, DateTimeOffset At)> cache = new(StringComparer.Ordinal);

    public bool IsEnforced => !string.IsNullOrWhiteSpace(connectionString);

    public async Task<bool> IsAllowedAsync(string email, CancellationToken ct)
    {
        if (!IsEnforced)
        {
            return true;
        }

        var key = email.Trim().ToLowerInvariant();
        var now = clock.GetUtcNow();

        if (cache.TryGetValue(key, out var hit) && now - hit.At < CacheFor)
        {
            return hit.Allowed;
        }

        bool allowed;
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);
            await using var command = new NpgsqlCommand(
                "SELECT is_allowed_for_hybrid AND NOT is_blocked FROM api_user WHERE email = @email", connection);
            command.Parameters.AddWithValue("email", key);
            allowed = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // Closed, not open: a workspace that names people and cannot say who may read it says no.
            logger.LogError(ex, "Could not read the hybrid grant of {Email} from the SancMeld accounts database; refusing.", key);
            return false;
        }

        cache[key] = (allowed, now);
        return allowed;
    }
}
