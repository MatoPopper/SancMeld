using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.Wikidata;

public static class WikidataServiceCollectionExtensions
{
    public static IServiceCollection AddWikidataConnector(
        this IServiceCollection services,
        IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<WikidataOptions>()
            .Bind(configuration.GetSection(WikidataOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(WikidataSparqlClient.HttpClientName, (provider, client) =>
        {
            var settings = provider.GetRequiredService<IOptions<WikidataOptions>>().Value;
            client.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        });

        // One client for the process: it is what keeps the queries one at a time and
        // spaced, and that only works if every caller shares it.
        services.AddSingleton<ISparqlClient, WikidataSparqlClient>();
        services.AddScoped<WikidataSyncService>();

        // The import asks for the listings it just wrote; the same service answers.
        services.AddScoped<IRelatedPartyEnricher>(sp => sp.GetRequiredService<WikidataSyncService>());

        return services;
    }
}
