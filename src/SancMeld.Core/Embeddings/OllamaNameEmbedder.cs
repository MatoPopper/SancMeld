using System.Net;
using System.Net.Http.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Core.Embeddings;

/// <summary>
/// Embeddings over Ollama's /api/embed.
///
/// The model name comes from the embedding_model row, not from configuration, so the
/// vectors written to the table are provably from the model the row describes.
/// </summary>
public sealed class OllamaNameEmbedder(HttpClient http, IOptions<SancMeldOptions> options, string modelName) : INameEmbedder
{
    public string ModelName => modelName;

    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        if (texts.Count == 0)
        {
            return [];
        }

        var endpoint = options.Value.Embeddings.Endpoint.TrimEnd('/');
        HttpResponseMessage response;

        try
        {
            response = await http
                .PostAsJsonAsync($"{endpoint}/api/embed", new { model = modelName, input = texts }, ct)
                .ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            throw new EmbeddingUnavailableException($"Could not reach the embedding endpoint at {endpoint}.", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new EmbeddingUnavailableException(
                    $"The endpoint at {endpoint} has no model '{modelName}'. Pull it first: ollama pull {modelName}");
            }

            if (!response.IsSuccessStatusCode)
            {
                throw new EmbeddingUnavailableException($"The embedding endpoint answered {(int)response.StatusCode}.");
            }

            var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct).ConfigureAwait(false)
                ?? throw new EmbeddingUnavailableException("Empty response from /api/embed.");

            if (body.Embeddings.Length != texts.Count)
            {
                throw new EmbeddingUnavailableException(
                    $"Asked for {texts.Count} embeddings, received {body.Embeddings.Length}.");
            }

            // Ollama normalises for some models and not others. Cosine as a plain dot
            // product needs unit length, so it is enforced rather than assumed.
            foreach (var vector in body.Embeddings)
            {
                EmbeddingVector.Normalize(vector);
            }

            return body.Embeddings;
        }
    }

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] float[][] Embeddings);
}

/// <summary>
/// The model could not be asked. One exception type so the matcher can treat every
/// variety the same way: log it, and carry on with the trigram alone.
/// </summary>
public sealed class EmbeddingUnavailableException(string message, Exception? inner = null) : Exception(message, inner);
