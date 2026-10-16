using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace SancMeld.Connectors.Rpvs;

public static class RpvsServiceCollectionExtensions
{
    public static IServiceCollection AddRpvsConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<RpvsOptions>()
            .Bind(configuration.GetSection(RpvsOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(RpvsClient.HttpClientName, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<RpvsOptions>>().Value.TimeoutSeconds);
        });

        services.AddSingleton<IRpvsClient, RpvsClient>();
        services.AddScoped<RpvsSyncService>();
        return services;
    }
}
