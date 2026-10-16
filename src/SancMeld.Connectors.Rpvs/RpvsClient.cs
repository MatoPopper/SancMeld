using System.Text.Json;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.Rpvs;

/// <summary>The two RPVS calls this connector makes. The seam the tests replace.</summary>
public interface IRpvsClient
{
    /// <summary>
    /// A page of partners with their companies and beneficial owners: the first page when
    /// <paramref name="nextLink"/> is null, otherwise the page the previous one pointed to.
    /// </summary>
    Task<JsonDocument> GetPartnersPageAsync(string? nextLink, CancellationToken ct = default);

    /// <summary>
    /// The ISO numeric code of the country in a partner's current address. The page feed
    /// cannot reach it (two levels of expansion), so it is asked for only for the few
    /// partners whose owner turned out to be listed.
    /// </summary>
    Task<string?> GetPartnerCountryCodeAsync(int partnerId, CancellationToken ct = default);
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

        return await GetAsync(url, ct).ConfigureAwait(false);
    }

    public async Task<string?> GetPartnerCountryCodeAsync(int partnerId, CancellationToken ct = default)
    {
        var url = $"{options.Value.BaseUrl.TrimEnd('/')}/PartneriVerejnehoSektora?$filter=Partner/Id eq {partnerId}&$expand=Adresa($expand=Stat)";
        using var doc = await GetAsync(url, ct).ConfigureAwait(false);

        if (!doc.RootElement.TryGetProperty("value", out var entries) || entries.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        // The current entry's address; an ended partner keeps the last one it had.
        string? code = null;
        foreach (var e in entries.EnumerateArray())
        {
            if (e.TryGetProperty("Adresa", out var a) && a.ValueKind == JsonValueKind.Object
                && a.TryGetProperty("Stat", out var s) && s.ValueKind == JsonValueKind.Object
                && s.TryGetProperty("StatistickyKod", out var k) && k.ValueKind == JsonValueKind.String)
            {
                code = k.GetString();
                if (!e.TryGetProperty("PlatnostDo", out var end) || end.ValueKind == JsonValueKind.Null)
                {
                    break;
                }
            }
        }

        return code;
    }

    private async Task<JsonDocument> GetAsync(string url, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", sancMeld.Value.Ingest.UserAgent);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");

        using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }
}
