using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Relations;

namespace SancMeld.Connectors.CzRegister;

public static class CzRegisterServiceCollectionExtensions
{
    /// <summary>Needs the ARES connector registered as well: the companies are read through it.</summary>
    public static IServiceCollection AddCzRegisterConnector(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddOptions<CzRegisterOptions>()
            .Bind(configuration.GetSection(CzRegisterOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddHttpClient(CzRegisterClient.HttpClientName, (provider, client) =>
        {
            client.Timeout = TimeSpan.FromSeconds(provider.GetRequiredService<IOptions<CzRegisterOptions>>().Value.TimeoutSeconds);
        });

        // A singleton, so that every lookup in the process keeps one pace towards justice.cz.
        services.AddSingleton<ICzRegisterClient, CzRegisterClient>();
        services.AddScoped<CzRegisterLookupService>();
        services.AddScoped<IRelatedPartyEnricher>(sp => sp.GetRequiredService<CzRegisterLookupService>());
        return services;
    }
}
