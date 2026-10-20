using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PepMeld.Core.Data;

/// <summary>
/// Used only by "dotnet ef" at design time. The connection string is never read from a
/// tracked file: supply PEPMELD_DESIGN_CONNECTION, or accept the local default, which is
/// the pepmeld database on the development container from docker-compose.yml.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PepMeldDbContext>
{
    public PepMeldDbContext CreateDbContext(string[] args)
    {
        var connection = Environment.GetEnvironmentVariable("PEPMELD_DESIGN_CONNECTION")
            ?? "Host=localhost;Port=55432;Database=pepmeld;Username=sancmeld;Password=sancmeld";

        var options = new DbContextOptionsBuilder<PepMeldDbContext>()
            .UsePepMeld(connection)
            .Options;

        return new PepMeldDbContext((DbContextOptions<PepMeldDbContext>)options);
    }
}
