using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace PepMeld.Api;

/// <summary>
/// Whether an address may screen for PEPs, answered by the account it signs in with: the
/// SancMeld user, whose flag is_allowed_for_pep is set on SancMeld's users screen. One
/// account and one grant for both services, kept where the users are managed, and read
/// here rather than copied, so a withdrawal takes effect within the cache below.
///
/// Configured with ConnectionStrings:SancMeldAccounts, a connection to SancMeld's
/// database; only api_user is read, and only two columns of it. Without it there is no
/// such check and every verified address may screen, which is what a development host
/// with no SancMeld beside it needs; a production host sets it.
/// </summary>
public sealed class PepAccess(IConfiguration configuration, TimeProvider clock, ILogger<PepAccess> logger)
{
    public const string ConnectionStringName = "SancMeldAccounts";

    /// <summary>A minute: long enough that a check's polling does not query per request, short enough for a withdrawal.</summary>
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
                "SELECT is_allowed_for_pep AND NOT is_blocked FROM api_user WHERE email = @email", connection);
            command.Parameters.AddWithValue("email", key);
            allowed = await command.ExecuteScalarAsync(ct).ConfigureAwait(false) is true;
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            // Closed, not open: a compliance tool that cannot say who may use it says no.
            logger.LogError(ex, "Could not read the PEP grant of {Email} from the SancMeld accounts database; refusing.", key);
            return false;
        }

        cache[key] = (allowed, now);
        return allowed;
    }
}
