using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using PepMeld.Connectors.CzPsp;
using PepMeld.Connectors.SkNrsr;
using PepMeld.Core.Checks;
using PepMeld.Core.Configuration;
using PepMeld.Core.Data;
using PepMeld.Core.Lists;

namespace PepMeld.Api;

/// <summary>
/// PepMeld's API is a library, as SancMeld's is: the host calls two methods and adds
/// what is a hosting concern (the worker, the scheduler, Swagger, CORS).
///
///   builder.Services.AddPepMeld(builder.Configuration);
///   builder.Services.AddPepMeldAuth(builder.Configuration);
///   app.UseAuthentication().UseAuthorization();
///   app.MapPepMeld();
/// </summary>
public static class PepMeldExtensions
{
    public const string ConnectionStringName = "PepMeld";

    public static IServiceCollection AddPepMeld(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<PepMeldOptions>()
            .Bind(configuration.GetSection(PepMeldOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 Connection string '{ConnectionStringName}' is not configured.

                 appsettings.json ships one pointing at the pepmeld database on the
                 development server in docker-compose.yml. Supply your own with any of:

                   development   dotnet user-secrets set "ConnectionStrings:{ConnectionStringName}" "Host=...;Database=pepmeld;..."
                   docker / prod  ConnectionStrings__{ConnectionStringName}=Host=...;Database=pepmeld;...
                   local file     appsettings.Development.json  (git-ignored)
                 """);
        }

        services.AddDbContext<PepMeldDbContext>(o => o.UsePepMeld(connectionString));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ISnapshotStore, FileSystemSnapshotStore>();
        services.AddScoped<PepImportService>();
        services.AddScoped<SourceQueryService>();

        services.AddSingleton<CheckQueue>();
        services.AddScoped<CheckService>();
        services.AddScoped<CheckRunner>();
        services.AddScoped<PepListMatcher>();

        // The stages that exist. Boards and Family are reported as skipped until they do.
        services.AddScoped<ICheckStage, ListsStage>();

        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        services.AddCzPspConnector(configuration);
        services.AddSkNrsrConnector(configuration);
        return services;
    }

    public static IEndpointRouteBuilder MapPepMeld(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("Health");
        endpoints.MapSources();
        endpoints.MapChecks();
        return endpoints;
    }
}
