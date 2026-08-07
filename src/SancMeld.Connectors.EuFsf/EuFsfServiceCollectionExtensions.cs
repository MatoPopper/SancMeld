using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.EuFsf;

/// <summary>
/// Each connector registers itself, so adding a source is one line in the host and
/// never an edit to shared wiring.
/// </summary>
public static class EuFsfServiceCollectionExtensions
{
    public const string HttpClientName = "SancMeld.EuFsf";

    public static IServiceCollection AddEuFsfConnector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<EuFsfOptions>()
            .Bind(configuration.GetSection(EuFsfOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest;

            // The default 100 seconds is not enough for 25 MB over a government link on a
            // bad day, and a timeout mid-transfer looks exactly like a truncated file.
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
        });

        // Transient, with the client resolved per instance: a singleton holding one
        // HttpClient forever is how connection pools end up pinned to a stale DNS answer,
        // which for a daily job would go unnoticed for as long as it kept working.
        services.AddTransient<ISanctionsConnector>(provider => new EuFsfConnector(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<IOptions<EuFsfOptions>>().Value,
            provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest));

        return services;
    }
}
