using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.UnSc;

/// <summary>
/// Each connector registers itself, so adding a source is one line in the host and
/// never an edit to shared wiring.
/// </summary>
public static class UnScServiceCollectionExtensions
{
    public const string HttpClientName = "SancMeld.UnSc";

    public static IServiceCollection AddUnScConnector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<UnScOptions>()
            .Bind(configuration.GetSection(UnScOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest;

            // The UN file is 2.2 MB, but the same generous timeout applies: a timeout
            // mid-transfer looks exactly like a truncated file.
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
        });

        // Transient, with the client resolved per instance: a singleton holding one
        // HttpClient forever is how connection pools end up pinned to a stale DNS answer,
        // which for a daily job would go unnoticed for as long as it kept working.
        services.AddTransient<ISanctionsConnector>(provider => new UnScConnector(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<IOptions<UnScOptions>>().Value,
            provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest));

        return services;
    }
}
