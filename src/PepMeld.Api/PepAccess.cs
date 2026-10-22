using System.Collections.Concurrent;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;
using PepMeld.Core.Access;

namespace PepMeld.Api;

/// <summary>
/// Whether an address may screen for PEPs, and the budget it spends doing so, both
/// answered by the account it signs in with: the SancMeld user, whose flag
/// is_allowed_for_pep and whose request_limit are set on SancMeld's users screen. One
/// account, one grant and one budget for both services, kept where the users are managed
/// and read here rather than copied, so a withdrawal takes effect within the cache below
/// and a check and a screening draw on the same number the users screen shows.
///
/// Configured with ConnectionStrings:SancMeldAccounts, a connection to SancMeld's
/// database; only api_user is touched: two columns read for the grant, requests_used
/// raised for a check. Without it there is neither check nor budget and every verified
/// address may screen without limit, which is what a development host with no SancMeld
/// beside it needs; a production host sets it.
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

        var key = Normalise(email);
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

    /// <summary>
    /// Spends one request of the SancMeld budget: one UPDATE that only succeeds while the
    /// budget lasts, the same statement SancMeld's own directory runs for a screening, so
    /// two checks at once cannot both take the last one. Never cached: the count is the
    /// point. Null when the accounts database cannot be reached.
    /// </summary>
    public async Task<QuotaState?> TryConsumeAsync(string email, CancellationToken ct)
    {
        if (!IsEnforced)
        {
            return QuotaState.Unlimited;
        }

        var key = Normalise(email);
        try
        {
            await using var connection = new NpgsqlConnection(connectionString);
            await connection.OpenAsync(ct).ConfigureAwait(false);

            await using var spend = new NpgsqlCommand(
                "UPDATE api_user SET requests_used = requests_used + 1 " +
                "WHERE email = @email AND requests_used < request_limit AND NOT is_blocked " +
                "RETURNING request_limit, requests_used", connection);
            spend.Parameters.AddWithValue("email", key);
            await using (var spent = await spend.ExecuteReaderAsync(ct).ConfigureAwait(false))
            {
                if (await spent.ReadAsync(ct).ConfigureAwait(false))
                {
                    return new QuotaState(true, spent.GetInt32(0), spent.GetInt32(1));
                }
            }

            await using var state = new NpgsqlCommand("SELECT request_limit, requests_used FROM api_user WHERE email = @email", connection);
            state.Parameters.AddWithValue("email", key);
            await using var row = await state.ExecuteReaderAsync(ct).ConfigureAwait(false);
            return await row.ReadAsync(ct).ConfigureAwait(false)
                ? new QuotaState(false, row.GetInt32(0), row.GetInt32(1))
                : new QuotaState(false, 0, 0);
        }
        catch (Exception ex) when (ex is NpgsqlException or InvalidOperationException)
        {
            logger.LogError(ex, "Could not spend a request of {Email} in the SancMeld accounts database.", key);
            return null;
        }
    }

    private static string Normalise(string email) => email.Trim().ToLowerInvariant();
}
