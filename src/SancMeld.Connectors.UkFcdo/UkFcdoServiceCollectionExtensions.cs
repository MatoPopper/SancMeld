using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.UkFcdo;

/// <summary>
/// Each connector registers itself, so adding a source is one line in the host and
/// never an edit to shared wiring.
/// </summary>
public static class UkFcdoServiceCollectionExtensions
{
    public const string HttpClientName = "SancMeld.UkFcdo";

    public static IServiceCollection AddUkFcdoConnector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<UkFcdoOptions>()
            .Bind(configuration.GetSection(UkFcdoOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest;
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
        });

        services.AddTransient<ISanctionsConnector>(provider => new UkFcdoConnector(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<IOptions<UkFcdoOptions>>().Value,
            provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest));

        return services;
    }
}
