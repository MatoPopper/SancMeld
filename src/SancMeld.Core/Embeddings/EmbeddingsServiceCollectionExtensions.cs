using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;

namespace SancMeld.Core.Embeddings;

public static class EmbeddingsServiceCollectionExtensions
{
    /// <summary>
    /// Registered whatever the mode. With Mode = Off nothing here is ever called into the
    /// network, but the import and the screening still take their dependencies, and a
    /// service graph that changes shape with a setting is one that fails at runtime in
    /// the configuration nobody tested.
    /// </summary>
    public static IServiceCollection AddSancMeldEmbeddings(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(nameof(OllamaNameEmbedder), (sp, http) =>
        {
            var settings = sp.GetRequiredService<IOptions<SancMeldOptions>>().Value.Embeddings;
            http.Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds);
        });

        // The model name is read from embedding_model so that the vectors stored are
        // provably from the model the row describes; configuration only chooses the row.
        services.AddScoped<INameEmbedder>(sp =>
        {
            var options = sp.GetRequiredService<IOptions<SancMeldOptions>>();
            var db = sp.GetRequiredService<SancMeldDbContext>();
            var code = options.Value.Embeddings.ModelCode;

            var modelName = db.EmbeddingModels
                .Where(m => m.IsActive && m.Code == code)
                .OrderByDescending(m => m.CreatedAt)
                .Select(m => m.ModelName)
                .FirstOrDefault() ?? code;

            var http = sp.GetRequiredService<IHttpClientFactory>().CreateClient(nameof(OllamaNameEmbedder));
            return new OllamaNameEmbedder(http, options, modelName);
        });

        services.AddSingleton<SemanticNameIndex>();
        services.AddScoped<NameEmbeddingStore>();
        services.AddScoped<SemanticCandidateSource>();

        return services;
    }
}
