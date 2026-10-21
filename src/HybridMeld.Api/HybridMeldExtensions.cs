using System.Text.Json.Serialization;
using HybridMeld.Core.Configuration;
using HybridMeld.Core.Data;
using HybridMeld.Core.Graph;
using HybridMeld.Core.Links;
using HybridMeld.Core.Seed;
using HybridMeld.Core.Sources;
using HybridMeld.Core.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;

namespace HybridMeld.Api;

/// <summary>
/// HybridMeld's API is a library, as the two sibling APIs are: the host calls two methods
/// and adds what is a hosting concern (the scheduler, Swagger, CORS).
///
///   builder.Services.AddHybridMeld(builder.Configuration);
///   builder.Services.AddHybridMeldAuth(builder.Configuration);
///   app.UseAuthentication().UseAuthorization();
///   app.MapHybridMeld();
/// </summary>
public static class HybridMeldExtensions
{
    public const string ConnectionStringName = "HybridMeld";

    public static IServiceCollection AddHybridMeld(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<HybridMeldOptions>()
            .Bind(configuration.GetSection(HybridMeldOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        var connectionString = configuration.GetConnectionString(ConnectionStringName);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"""
                 Connection string '{ConnectionStringName}' is not configured.

                 appsettings.json ships one pointing at the hybridmeld database on the
                 development server in docker-compose.yml. Supply your own with any of:

                   development   dotnet user-secrets set "ConnectionStrings:{ConnectionStringName}" "Host=...;Database=hybridmeld;..."
                   docker / prod  ConnectionStrings__{ConnectionStringName}=Host=...;Database=hybridmeld;...
                   local file     appsettings.Development.json  (git-ignored)
                 """);
        }

        services.AddDbContext<HybridMeldDbContext>(o => o.UseHybridMeld(connectionString));
        services.TryAddSingleton(TimeProvider.System);

        services.AddSingleton<ISnapshotStore, FileSystemSnapshotStore>();
        services.AddSingleton<LinkResolver>();
        services.AddScoped<LinkVerifier>();
        services.AddScoped<RegimeSeeder>();

        services.AddScoped<CampaignService>();
        services.AddScoped<ActorService>();
        services.AddScoped<ClaimService>();
        services.AddScoped<FeedService>();
        services.AddScoped<HybridGraphBuilder>();

        services.AddScoped<SourceService>();
        services.AddScoped<SourceFetchService>();
        services.AddScoped<ISourceFetcher, RssFetcher>();
        services.AddScoped<ISourceFetcher, TelegramFetcher>();
        services.AddScoped<ISourceFetcher, HtmlFetcher>();
        services.AddScoped<PageFetcher>();
        services.AddHttpClient(PageFetcher.HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<HybridMeldOptions>>().Value.Ingest;
            client.Timeout = TimeSpan.FromSeconds(ingest.FetchTimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ingest.UserAgent);
            client.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "sk,cs,en;q=0.8");
        });

        services.ConfigureHttpJsonOptions(json =>
        {
            json.SerializerOptions.Converters.Add(new JsonStringEnumConverter());
            json.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
        });

        return services;
    }

    public static IEndpointRouteBuilder MapHybridMeld(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        endpoints.MapGet("/health", () => Results.Ok(new { status = "ok" })).WithName("Health");
        endpoints.MapWorkspace();
        endpoints.MapSources();
        return endpoints;
    }
}
