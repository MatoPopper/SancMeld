using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace HybridMeld.Core.Data;

/// <summary>
/// Used only by "dotnet ef" at design time. Supply HYBRIDMELD_DESIGN_CONNECTION, or
/// accept the local default: the hybridmeld database on the development container from
/// docker-compose.yml.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<HybridMeldDbContext>
{
    public HybridMeldDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("HYBRIDMELD_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=55432;Database=hybridmeld;Username=sancmeld;Password=sancmeld";

        var options = new DbContextOptionsBuilder<HybridMeldDbContext>()
            .UseHybridMeld(connection)
            .Options;

        return new HybridMeldDbContext((DbContextOptions<HybridMeldDbContext>)options);
    }
}
