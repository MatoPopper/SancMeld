using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SancMeld.Connectors.Ares;

public static class AresServiceCollectionExtensions
{
    public static IServiceCollection AddAresConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<AresOptions>()
            .Bind(configuration.GetSection(AresOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(AresClient.HttpClientName, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<AresOptions>>().Value.TimeoutSeconds);
        });

        services.AddSingleton<IAresClient, AresClient>();
        services.AddScoped<AresLookupService>();
        return services;
    }
}
