using Microsoft.EntityFrameworkCore;
using Npgsql;
using SancMeld.Core.Data;
using Xunit;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// These tests run against a real PostgreSQL because what they check does not exist
/// anywhere else: the partial unique index that permits one open revision per record,
/// jsonb staging, and ExecuteUpdate. An in-memory provider would pass while the schema
/// that actually ships was broken.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "SANCMELD_TEST_CONNECTION";

    private const string LocalDefault =
        "Host=localhost;Port=55432;Database=sancmeld_tests;Username=sancmeld;Password=sancmeld";

    public string ConnectionString { get; } =
        Environment.GetEnvironmentVariable(ConnectionVariable) ?? LocalDefault;

    public async Task InitializeAsync()
    {
        await EnsureDatabaseExistsAsync();

        await using var db = CreateContext();

        try
        {
            await db.Database.MigrateAsync();
        }
        catch (NpgsqlException ex)
        {
            throw new InvalidOperationException(
                $"""
                 Could not reach the test database.

                   {ConnectionString}

                 Start it with 'docker compose up -d db' from the repository root, or point
                 {ConnectionVariable} at another PostgreSQL 16+ instance. These tests need a
                 real server; see CONTRIBUTING.md.
                 """, ex);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public SancMeldDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<SancMeldDbContext>()
            .UseSancMeld(ConnectionString)
            .Options as DbContextOptions<SancMeldDbContext>
            ?? throw new InvalidOperationException("Failed to build the context options."));

    /// <summary>
    /// A database of its own, so a test run can never touch the development data sitting
    /// in the compose container next to it.
    /// </summary>
    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var database = builder.Database!;
        builder.Database = "postgres";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var exists = new NpgsqlCommand(
            "SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);

        if (await exists.ExecuteScalarAsync() is not null)
        {
            return;
        }

        // Identifier quoting rather than a parameter: CREATE DATABASE takes no parameters,
        // and the name comes from our own connection string, not from a request.
        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", connection);
        await create.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "postgres";
}
