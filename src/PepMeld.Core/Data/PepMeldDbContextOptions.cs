using Microsoft.EntityFrameworkCore;

namespace PepMeld.Core.Data;

/// <summary>
/// Every <see cref="PepMeldDbContext"/> is built through here, design time and runtime
/// alike, for the same reason SancMeld has one: the migrations history table and the
/// snake_case naming are part of the schema contract, and a host that forgets one of
/// them fails deep inside an unrelated query rather than at startup.
/// </summary>
public static class PepMeldDbContextOptions
{
    public const string MigrationsHistoryTable = "__migrations";

    public static DbContextOptionsBuilder UsePepMeld(this DbContextOptionsBuilder builder, string connectionString)
    {
        ArgumentNullException.ThrowIfNull(builder);

        return builder
            .UseNpgsql(connectionString, o => o.MigrationsHistoryTable(MigrationsHistoryTable))
            .UseSnakeCaseNamingConvention();
    }
}
