using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.SkRegister;

public static class SkRegisterServiceCollectionExtensions
{
    public static IServiceCollection AddSkRegisterConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<SkRegisterOptions>()
            .Bind(configuration.GetSection(SkRegisterOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(SkRegisterClient.HttpClientName, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<SkRegisterOptions>>().Value.TimeoutSeconds);
        });

        // A singleton, so that every lookup in the process keeps one pace towards ORSR.
        services.AddSingleton<ISkRegisterClient, SkRegisterClient>();
        services.AddScoped<SkRegisterLookupService>();
        services.AddScoped<IRelatedPartyEnricher>(sp => sp.GetRequiredService<SkRegisterLookupService>());
        return services;
    }
}
