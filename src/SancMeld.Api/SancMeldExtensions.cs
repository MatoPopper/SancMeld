using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SancMeld.Connectors.CzMzv;
using SancMeld.Connectors.EuFsf;
using SancMeld.Connectors.FrGels;
using SancMeld.Connectors.NlBz;
using SancMeld.Connectors.PlMswia;
using SancMeld.Connectors.UkFcdo;
using SancMeld.Connectors.UnSc;
using SancMeld.Connectors.Wikidata;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Embeddings;
using SancMeld.Core.Ingest;
using SancMeld.Core.Learning;
using SancMeld.Core.Relations;
using SancMeld.Core.Search;

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

        // Fail fast and explain how to fix it. The default framework error for this
        // ("Value cannot be null") tells the reader nothing, and a host without a
        // database is the single most common first-run problem.
        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 Connection string '{ConnectionStringName}' is not configured.

                 appsettings.json ships one pointing at the development database in
                 docker-compose.yml, so this means it was removed or overridden. Either
                 restore it, or supply your own — any of these wins over appsettings.json:

                   development   dotnet user-secrets set "ConnectionStrings:{ConnectionStringName}" \
                                     "Host=localhost;Database=sancmeld;Username=sancmeld;Password=..."

                   docker / prod  environment variable
                                     ConnectionStrings__{ConnectionStringName}=Host=...;Database=...

                   local file     appsettings.Development.json  (git-ignored)

                 See README.md -> Configuration.
                 """);
        }

        // UseSancMeld and nothing else: it carries the snake_case naming and the migrations
        // history table the migrated schema depends on.
        services.AddDbContext<SancMeldDbContext>(o => o.UseSancMeld(connectionString));

        services.TryAddSingleton(TimeProvider.System);
        services.AddSingleton<ISnapshotStore, FileSystemSnapshotStore>();
        services.AddScoped<SanctionsImportService>();

        services.AddScoped<DesignationQueryService>();
        services.AddScoped<IScoringStrategy, RulesScoringStrategy>();
        services.AddSancMeldEmbeddings();
        services.AddScoped<RelatedPartyStore>();
        services.AddScoped<RelatedPartyQueryService>();
        services.AddScoped<DesignationGraphService>();
        services.AddScoped<ScreeningService>();
        services.AddScoped<ComplianceCheckService>();
        services.AddScoped<AdjudicationService>();
        services.AddScoped<XrefTrainingSeeder>();

        // Enums as text, matching how the database stores them. "legalEffect": 0 tells a
        // consumer nothing, and worse, silently changes meaning if a member is ever
        // inserted into the middle of an enum.
        services.ConfigureHttpJsonOptions(json =>
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter()));

        services.AddEuFsfConnector(configuration);
        services.AddUnScConnector(configuration);
        services.AddFrGelsConnector(configuration);
        services.AddPlMswiaConnector(configuration);
        services.AddCzMzvConnector(configuration);
        services.AddNlBzConnector(configuration);
        services.AddUkFcdoConnector(configuration);

        // Not a sanctions source: a register of who owns and runs what, read for the
        // listed persons the lists already hold.
        services.AddWikidataConnector(configuration);

        // TODO: adjudication capture, and scheduled runs of the import.
        // Defaults are single-tenant and unauthenticated by design; the hosted
        // edition overrides ITenantResolver, IScoringStrategy and IQuotaPolicy.
        return services;
    }

    public static IEndpointRouteBuilder MapSancMeld(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" }))
                 .WithName("Health");

        // Whether a sign-in exists at all decides two things: whether the endpoints that
        // write require one, and whether the endpoints that manage access are mapped.
        //
        // Without it nothing changes from the unauthenticated default the open-source
        // edition documents. Mapping /auth and /admins anyway would produce endpoints that
        // fail on every call — requiring authorisation against a scheme nobody registered
        // is a 500, not a 401 — and a host with no allow list has nothing to manage, which
        // is what 404 says.
        var signInConfigured = endpoints.ServiceProvider.GetService<SancMeldAuthMarker>() is not null;

        endpoints.MapDesignations();
        endpoints.MapScreening(signInConfigured);
        endpoints.MapAdjudications(signInConfigured);
        endpoints.MapTrainingData();

        if (signInConfigured)
        {
            endpoints.MapSancMeldAuth();
            endpoints.MapAdmins();
        }

        // TODO: /sources
        return endpoints;
    }
}
