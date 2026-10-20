using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.CzPsp;

public static class CzPspServiceCollectionExtensions
{
    public static IServiceCollection AddCzPspConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CzPspOptions>()
            .Bind(configuration.GetSection(CzPspOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(CzPspConnector.HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<PepMeldOptions>>().Value.Ingest;
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ingest.UserAgent);
        });

        services.AddSingleton<IPepListConnector, CzPspConnector>();
        return services;
    }
}
