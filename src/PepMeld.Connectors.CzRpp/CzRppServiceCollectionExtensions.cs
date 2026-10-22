using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.CzRpp;

public static class CzRppServiceCollectionExtensions
{
    public static IServiceCollection AddCzRppConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CzRppOptions>()
            .Bind(configuration.GetSection(CzRppOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(CzRppHeadsConnector.HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<PepMeldOptions>>().Value.Ingest;
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ingest.UserAgent);
        });

        services.AddSingleton<IPepListConnector, CzRppHeadsConnector>();
        return services;
    }
}
