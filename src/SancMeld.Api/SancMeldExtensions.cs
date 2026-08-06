using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using SancMeld.Core.Configuration;

namespace SancMeld.Api;

/// <summary>
/// SancMeld's API is a LIBRARY, not an application.
///
/// This is what lets the closed-source hosted edition reuse the open-source core
/// without forking it: both hosts call the same two methods and the hosted one
/// simply layers authentication, tenancy and quotas on top.
///
///   // open source host
///   builder.Services.AddSancMeld(builder.Configuration);
///   app.MapSancMeld();
///
///   // hosted edition
///   builder.Services.AddSancMeld(builder.Configuration);
///   builder.Services.AddGoogleAuth(builder.Configuration);
///   builder.Services.AddTenancy();
///   app.UseAuthentication().UseAuthorization();
///   app.UseTenantResolution();
///   app.MapSancMeld();
///   app.MapCloudEndpoints();
///
/// If the hosted edition ever needs to patch the core, that is a signal the core
/// is missing an extension point — add it here rather than forking.
/// </summary>
public static class SancMeldExtensions
{
    /// <summary>Configuration key for the database connection, by .NET convention.</summary>
    public const string ConnectionStringName = "SancMeld";

    public static IServiceCollection AddSancMeld(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SancMeldOptions>()
            .Bind(configuration.GetSection(SancMeldOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        // Fail fast and explain how to fix it. A missing connection string is the
        // single most common first-run problem, and the default framework error
        // ("Value cannot be null") tells the reader nothing.
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 Connection string '{ConnectionStringName}' is not configured.

                 It is intentionally absent from appsettings.json so that a database
                 password can never be committed by accident. Supply it in one of:

                   development   dotnet user-secrets set "ConnectionStrings:{ConnectionStringName}" \
                                     "Host=localhost;Database=sancmeld;Username=sancmeld;Password=..."

                   docker / prod  environment variable
                                     ConnectionStrings__{ConnectionStringName}=Host=...;Database=...

                   local file     appsettings.Development.json  (git-ignored)

                 See README.md -> Configuration.
                 """);
        }

        // TODO: register data access, connectors, matching engine and ingest jobs.
        // The DbContext must be registered through SancMeldDbContextOptions.UseSancMeld() —
        // it carries the snake_case naming and the migrations history table that the
        // migrated schema depends on.
        // Defaults are single-tenant and unauthenticated by design; the hosted
        // edition overrides ITenantResolver, IScoringStrategy and IQuotaPolicy.
        return services;
    }

    public static IEndpointRouteBuilder MapSancMeld(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }))
                 .WithName("Health");

        // TODO: /screen, /designations/{id}, /sources, /adjudications
        return endpoints;
    }
}
