using Microsoft.EntityFrameworkCore;

namespace SancMeld.Core.Data;

/// <summary>
/// Every <see cref="SancMeldDbContext"/> is built through here — design time and
/// runtime alike.
///
/// Both settings below are part of the schema contract, not preferences. The
/// migrated database records its history in __migrations and names every table and
/// column in snake_case. A host that configures only one of them, or neither,
/// produces SQL that does not match the database it is pointed at — and the failure
/// shows up as a missing relation deep inside an unrelated query, not at startup.
/// </summary>
public static class SancMeldDbContextOptions
{
    public const string MigrationsHistoryTable = "__migrations";

    public static DbContextOptionsBuilder UseSancMeld(
        this DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(MigrationsHistoryTable))
            // snake_case: idiomatic in PostgreSQL and keeps the raw SQL in migrations
            // readable without quoting every identifier.
            .UseSnakeCaseNamingConvention();
    }
}
