using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SancMeld.Core.Data;

/// <summary>
/// Used only by "dotnet ef" at design time. The connection string is never read
/// from a tracked file: supply it via the SANCMELD_DESIGN_CONNECTION environment
/// variable, or accept the local default below, which points at the development
/// container from docker-compose.yml.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SancMeldDbContext>
{
    public SancMeldDbContext CreateDbContext(string[] args)
    {
        // Port 55432 is the docker-compose container, not a default local PostgreSQL.
        var connection = Environment.GetEnvironmentVariable("SANCMELD_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=55432;Database=sancmeld;Username=sancmeld;Password=sancmeld";

        var options = new DbContextOptionsBuilder<SancMeldDbContext>()
            .UseSancMeld(connection)
            .Options;

        return new SancMeldDbContext((DbContextOptions<SancMeldDbContext>)options);
    }
}
