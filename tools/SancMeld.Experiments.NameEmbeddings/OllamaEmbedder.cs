using System.Net;
using System.Net.Http.Json;
using System.Numerics.Tensors;
using System.Text.Json.Serialization;

namespace SancMeld.Experiments.NameEmbeddings;

/// <summary>
/// Embeddings over Ollama's HTTP API.
///
/// HTTP rather than ONNX in-process, for an experiment: swapping the model is one flag and
/// one <c>ollama pull</c>, with no tokenizer to wire up per model family. Should a model
/// earn its place, the production path is ONNX Runtime inside the API process — a matcher
/// that depends on a second service being up is a matcher that is sometimes off.
/// </summary>
public sealed class OllamaEmbedder(Uri endpoint, string model) : IDisposable
{
    private readonly HttpClient _http = new()
    {
        BaseAddress = endpoint,
        // A batch on a CPU-only machine can legitimately take minutes.
        Timeout = TimeSpan.FromMinutes(10),
    };

    public string Model => model;

    /// <summary>Unit-length vectors, one per text, in order.</summary>
    public async Task<float[][]> EmbedAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        HttpResponseMessage response;

        try
        {
            response = await _http.PostAsJsonAsync("/api/embed", new { model, input = texts }, ct);
        }
        catch (HttpRequestException ex)
        {
            throw new InvalidOperationException(
                $"Could not reach Ollama at {endpoint}. Is it running? ({ex.Message})", ex);
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.NotFound)
            {
                throw new InvalidOperationException(
                    $"Ollama at {endpoint} has no model '{model}'. Pull it first: ollama pull {model}");
            }

            response.EnsureSuccessStatusCode();

            var body = await response.Content.ReadFromJsonAsync<EmbedResponse>(ct)
                ?? throw new InvalidOperationException("Empty response from /api/embed.");

            if (body.Embeddings.Length != texts.Count)
            {
                throw new InvalidOperationException(
                    $"Asked for {texts.Count} embeddings, received {body.Embeddings.Length}.");
            }

            // Ollama normalises for some models and not others. Cosine similarity as a plain
            // dot product needs unit length, so it is enforced here rather than assumed.
            foreach (var vector in body.Embeddings)
            {
                var norm = TensorPrimitives.Norm(vector);
                if (norm > 0)
                {
                    TensorPrimitives.Divide(vector, norm, vector);
                }
            }

            return body.Embeddings;
        }
    }

    public void Dispose() => _http.Dispose();

    private sealed record EmbedResponse([property: JsonPropertyName("embeddings")] float[][] Embeddings);
}
