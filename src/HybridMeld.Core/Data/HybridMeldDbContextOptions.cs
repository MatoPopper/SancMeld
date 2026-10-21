using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Data;

/// <summary>
/// Every <see cref="HybridMeldDbContext"/> is built through here, design time and runtime
/// alike: the migrations history table and the snake_case naming are part of the schema
/// contract, and a host that forgets one of them fails deep inside a query, not at start.
/// </summary>
public static class HybridMeldDbContextOptions
{
    public const string MigrationsHistoryTable = "__migrations";

    public static DbContextOptionsBuilder UseHybridMeld(this DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention();
    }
}
