using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.Ares;

/// <summary>The two ARES calls this connector makes. The seam the tests replace.</summary>
public interface IAresClient
{
    /// <summary>The public-register record of one IČO, or null when ARES has none.</summary>
    Task<JsonDocument?> GetPublicRegisterAsync(string ico, CancellationToken ct = default);

    /// <summary>Subjects whose business name matches, as (IČO, name).</summary>
    Task<IReadOnlyList<(string Ico, string Name)>> SearchByNameAsync(string name, int max, CancellationToken ct = default);
}

public sealed class AresClient(IHttpClientFactory httpClients, IOptions<AresOptions> options, IOptions<SancMeldOptions> sancMeld) : IAresClient
{
    public const string HttpClientName = "SancMeld.Ares";

    public async Task<JsonDocument?> GetPublicRegisterAsync(string ico, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{options.Value.BaseUrl.TrimEnd('/')}/ekonomicke-subjekty-vr/{Uri.EscapeDataString(ico)}");
        Headers(request);

        using var response = await Http().SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<(string Ico, string Name)>> SearchByNameAsync(string name, int max, CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, $"{options.Value.BaseUrl.TrimEnd('/')}/ekonomicke-subjekty/vyhledat")
        {
            Content = JsonContent.Create(new { obchodniJmeno = name, pocet = max, start = 0 }),
        };
        Headers(request);

        using var response = await Http().SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return [];
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("ekonomickeSubjekty", out var subjects) || subjects.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<(string, string)>();
        foreach (var s in subjects.EnumerateArray())
        {
            if (s.TryGetProperty("ico", out var i) && s.TryGetProperty("obchodniJmeno", out var n)
                && i.GetString() is { } ico && n.GetString() is { } companyName)
            {
                result.Add((ico, companyName));
            }
        }

        return result;
    }

    private HttpClient Http() => httpClients.CreateClient(HttpClientName);

    private void Headers(HttpRequestMessage request)
    {
        request.Headers.TryAddWithoutValidation("User-Agent", sancMeld.Value.Ingest.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
    }
}
