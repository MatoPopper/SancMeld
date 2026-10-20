using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.Wikidata;

public static class WikidataServiceCollectionExtensions
{
    public static IServiceCollection AddWikidataPositionsConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WikidataPositionsOptions>()
            .Bind(configuration.GetSection(WikidataPositionsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(WikidataPositionsConnector.HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<PepMeldOptions>>().Value.Ingest;

            // The query service allows a minute per query and asks for a descriptive agent.
            client.Timeout = TimeSpan.FromSeconds(Math.Min(ingest.DownloadTimeoutSeconds, 90));
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ingest.UserAgent);
        });

        services.AddSingleton<IPepListConnector, WikidataPositionsConnector>();
        return services;
    }
}
