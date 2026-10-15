using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// Each connector registers itself, so adding a source is one line in the host and
/// never an edit to shared wiring.
/// </summary>
public static class CzMzvServiceCollectionExtensions
{
    public const string HttpClientName = "SancMeld.CzMzv";

    public static IServiceCollection AddCzMzvConnector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CzMzvOptions>()
            .Bind(configuration.GetSection(CzMzvOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest;
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
        });

        services.AddTransient<ISanctionsConnector>(provider => new CzMzvConnector(
            provider.GetRequiredService<IHttpClientFactory>().CreateClient(HttpClientName),
            provider.GetRequiredService<IOptions<CzMzvOptions>>().Value,
            provider.GetRequiredService<IOptions<SancMeldOptions>>().Value.Ingest));

        return services;
    }
}
