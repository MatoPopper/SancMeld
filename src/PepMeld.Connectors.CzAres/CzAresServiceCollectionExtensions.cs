using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.CzAres;

public static class CzAresServiceCollectionExtensions
{
    public static IServiceCollection AddCzAresConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CzAresOptions>()
            .Bind(configuration.GetSection(CzAresOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(CzAresStateBoardsConnector.HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<PepMeldOptions>>().Value.Ingest;
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ingest.UserAgent);
        });

        services.AddSingleton<IPepListConnector, CzAresStateBoardsConnector>();
        return services;
    }
}
