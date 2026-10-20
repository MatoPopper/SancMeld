using Microsoft.EntityFrameworkCore;
using Npgsql;
using PepMeld.Core.Data;
using Xunit;

namespace PepMeld.Core.IntegrationTests;

/// <summary>
/// A real PostgreSQL, for the same reason SancMeld's integration tests use one: what
/// they check lives in the database. The partial unique index that allows one running
/// check per user, the trigram operators, ExecuteUpdate. The database is the tests' own
/// on the development server, never the development data.
/// </summary>
public sealed class DatabaseFixture : IAsyncLifetime
{
    public const string ConnectionVariable = "PEPMELD_TEST_CONNECTION";

    private const string LocalDefault =
        "Host=localhost;Port=55432;Database=pepmeld_tests;Username=sancmeld;Password=sancmeld";

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
                 {ConnectionVariable} at another PostgreSQL 16+ instance.
                 """, ex);
        }
    }

    public Task DisposeAsync() => Task.CompletedTask;

    public PepMeldDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<PepMeldDbContext>()
            .UsePepMeld(ConnectionString)
            .Options as DbContextOptions<PepMeldDbContext>
            ?? throw new InvalidOperationException("Failed to build the context options."));

    private async Task EnsureDatabaseExistsAsync()
    {
        var builder = new NpgsqlConnectionStringBuilder(ConnectionString);
        var database = builder.Database!;
        builder.Database = "postgres";

        await using var connection = new NpgsqlConnection(builder.ConnectionString);
        await connection.OpenAsync();

        await using var exists = new NpgsqlCommand("SELECT 1 FROM pg_database WHERE datname = @name", connection);
        exists.Parameters.AddWithValue("name", database);

        if (await exists.ExecuteScalarAsync() is not null)
        {
            return;
        }

        await using var create = new NpgsqlCommand($"CREATE DATABASE \"{database.Replace("\"", "\"\"")}\"", connection);
        await create.ExecuteNonQueryAsync();
    }
}

[CollectionDefinition(Name)]
public sealed class DatabaseCollection : ICollectionFixture<DatabaseFixture>
{
    public const string Name = "postgres";
}
