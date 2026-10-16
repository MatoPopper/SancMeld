using System.Text.Json;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.Rpvs;

/// <summary>The one RPVS call this connector makes, page by page. The seam the tests replace.</summary>
public interface IRpvsClient
{
    /// <summary>
    /// A page of partners with their companies and beneficial owners: the first page when
    /// <paramref name="nextLink"/> is null, otherwise the page the previous one pointed to.
    /// </summary>
    Task<JsonDocument> GetPartnersPageAsync(string? nextLink, CancellationToken ct = default);
}

public sealed class RpvsClient(IHttpClientFactory httpClients, IOptions<RpvsOptions> options, IOptions<SancMeldOptions> sancMeld) : IRpvsClient
{
    public const string HttpClientName = "SancMeld.Rpvs";

    /// <summary>
    /// Firms and owners, with the owner's nationality. The service allows two levels of
    /// expansion, so the firm's address and country are out of reach; the paging link it
    /// returns keeps the expansion.
    /// </summary>
    internal const string FirstPage = "Partneri?$expand=PartneriVerejnehoSektora,KonecniUzivateliaVyhod($expand=StatnaPrislusnost)";

    public async Task<JsonDocument> GetPartnersPageAsync(string? nextLink, CancellationToken ct = default)
    {
        var baseUrl = options.Value.BaseUrl.TrimEnd('/');
        var url = nextLink ?? $"{baseUrl}/{FirstPage}";

        // The next link comes from the response; it is followed only while it stays on the
        // register's own service.
        if (!url.StartsWith(baseUrl + "/", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"RPVS returned a paging link outside {baseUrl}: {url}");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", sancMeld.Value.Ingest.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
