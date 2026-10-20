using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using PepMeld.Api;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// The PEP grant read from SancMeld's account table. Against SancMeld's own test database,
/// which its integration tests create and migrate; the rows made here are removed again.
/// </summary>
public sealed class PepAccessTests : IAsyncLifetime
{
    public const string ConnectionVariable = "PEPMELD_TEST_ACCOUNTS_CONNECTION";

    private readonly string connectionString = Environment.GetEnvironmentVariable(ConnectionVariable)
        ?? "Host=localhost;Port=55432;Database=sancmeld_tests;Username=sancmeld;Password=sancmeld";

    private readonly string allowed = $"pep.allowed.{Guid.NewGuid():N}@example.org";
    private readonly string blocked = $"pep.blocked.{Guid.NewGuid():N}@example.org";
    private readonly string notGranted = $"pep.no.{Guid.NewGuid():N}@example.org";

    public async Task InitializeAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);

        try
        {
            await connection.OpenAsync();
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException(
                $"SancMeld's test database is not there ({connectionString}). Run SancMeld.Core.IntegrationTests once, " +
                $"which creates and migrates it, or point {ConnectionVariable} at a migrated SancMeld database.", ex);
        }

        foreach (var (email, pep, isBlocked) in new[] { (allowed, true, false), (blocked, true, true), (notGranted, false, false) })
        {
            await using var insert = new NpgsqlCommand(
                "INSERT INTO api_user (id, email, request_limit, requests_used, is_allowed_for_additional_info, is_user_admin, is_system_admin, is_blocked, is_allowed_for_pep, created_at) " +
                "VALUES (@id, @email, 10, 0, false, false, false, @blocked, @pep, now())", connection);
            insert.Parameters.AddWithValue("id", Guid.NewGuid());
            insert.Parameters.AddWithValue("email", email);
            insert.Parameters.AddWithValue("blocked", isBlocked);
            insert.Parameters.AddWithValue("pep", pep);
            await insert.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync();
        await using var delete = new NpgsqlCommand("DELETE FROM api_user WHERE email = ANY(@emails)", connection);
        delete.Parameters.AddWithValue("emails", new[] { allowed, blocked, notGranted });
        await delete.ExecuteNonQueryAsync();
    }

    private PepAccess Access(string? connection) =>
        new(new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?> { [$"ConnectionStrings:{PepAccess.ConnectionStringName}"] = connection })
                .Build(),
            TimeProvider.System,
            NullLogger<PepAccess>.Instance);

    [Fact]
    public async Task OnlyAGrantedUnblockedAccountMayScreen()
    {
        var access = Access(connectionString);

        Assert.True(access.IsEnforced);
        Assert.True(await access.IsAllowedAsync(allowed.ToUpperInvariant(), CancellationToken.None));
        Assert.False(await access.IsAllowedAsync(blocked, CancellationToken.None));
        Assert.False(await access.IsAllowedAsync(notGranted, CancellationToken.None));
        Assert.False(await access.IsAllowedAsync("never.seen@example.org", CancellationToken.None));
    }

    [Fact]
    public async Task WithoutTheAccountsDatabaseEveryoneMay()
    {
        var access = Access(null);

        Assert.False(access.IsEnforced);
        Assert.True(await access.IsAllowedAsync(notGranted, CancellationToken.None));
    }

    [Fact]
    public async Task AnUnreachableAccountsDatabaseRefuses()
    {
        var access = Access("Host=127.0.0.1;Port=1;Database=none;Username=x;Password=x;Timeout=2");

        Assert.False(await access.IsAllowedAsync(allowed, CancellationToken.None));
    }
}
