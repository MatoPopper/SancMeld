using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.SkNrsr;

public static class SkNrsrServiceCollectionExtensions
{
    public static IServiceCollection AddSkNrsrConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SkNrsrOptions>()
            .Bind(configuration.GetSection(SkNrsrOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(SkNrsrConnector.HttpClientName, (provider, client) =>
        {
            var ingest = provider.GetRequiredService<IOptions<PepMeldOptions>>().Value.Ingest;
            client.Timeout = TimeSpan.FromSeconds(ingest.DownloadTimeoutSeconds);
            client.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", ingest.UserAgent);
        });

        services.AddSingleton<IPepListConnector, SkNrsrConnector>();
        return services;
    }
}
