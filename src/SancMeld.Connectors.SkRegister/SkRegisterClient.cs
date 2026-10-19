using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.SkRegister;

/// <summary>The ORSR and RPO calls this connector makes. The seam the tests replace.</summary>
public interface ISkRegisterClient
{
    /// <summary>One page (20 rows) of the ORSR search for a natural person in any role, and the total.</summary>
    Task<(IReadOnlyList<OrsrPersonRow> Rows, int Total)> SearchPersonAsync(string surname, string? givenName, int page, CancellationToken ct = default);

    /// <summary>The IČO on a company's ORSR extract, or null.</summary>
    Task<string?> GetIcoAsync(string id, string sid, CancellationToken ct = default);

    /// <summary>RPO companies whose name contains the text, current and past names alike.</summary>
    Task<IReadOnlyList<RpoSearchHit>> SearchCompaniesAsync(string name, CancellationToken ct = default);

    /// <summary>RPO companies with this IČO (one, or none).</summary>
    Task<IReadOnlyList<RpoSearchHit>> FindByIcoAsync(string ico, CancellationToken ct = default);

    /// <summary>One RPO company with its history: names, shareholders, statutory body.</summary>
    Task<RpoCompany?> GetCompanyAsync(long id, CancellationToken ct = default);
}

public sealed class SkRegisterClient(IHttpClientFactory httpClients, IOptions<SkRegisterOptions> options, IOptions<SancMeldOptions> sancMeld, TimeProvider clock)
    : ISkRegisterClient
{
    public const string HttpClientName = "SancMeld.SkRegister";

    /// <summary>ORSR speaks windows-1250, in its pages and in the query strings it accepts.</summary>
    private static readonly Encoding Windows1250 = CreateWindows1250();

    private readonly SemaphoreSlim _pace = new(1, 1);

    public async Task<(IReadOnlyList<OrsrPersonRow> Rows, int Total)> SearchPersonAsync(string surname, string? givenName, int page, CancellationToken ct = default)
    {
        var url = $"{Orsr}/hladaj_osoba.asp?PR={Encode(surname)}&MENO={Encode(givenName ?? string.Empty)}&SID=0&T=f0&R=on"
            + (page > 1 ? $"&STR={page}" : string.Empty);

        var html = await GetOrsrAsync(url, ct).ConfigureAwait(false);
        return html is null ? ([], 0) : (OrsrHtmlParser.PersonRows(html), OrsrHtmlParser.Total(html));
    }

    public async Task<string?> GetIcoAsync(string id, string sid, CancellationToken ct = default)
    {
        var html = await GetOrsrAsync($"{Orsr}/vypis.asp?ID={Uri.EscapeDataString(id)}&SID={Uri.EscapeDataString(sid)}&P=0", ct).ConfigureAwait(false);
        return html is null ? null : OrsrHtmlParser.Ico(html);
    }

    public async Task<IReadOnlyList<RpoSearchHit>> SearchCompaniesAsync(string name, CancellationToken ct = default)
    {
        using var doc = await GetRpoAsync($"search?fullName={Uri.EscapeDataString(name)}", ct).ConfigureAwait(false);
        return doc is null ? [] : RpoRecordParser.ParseSearch(doc.RootElement);
    }

    public async Task<IReadOnlyList<RpoSearchHit>> FindByIcoAsync(string ico, CancellationToken ct = default)
    {
        using var doc = await GetRpoAsync($"search?identifier={Uri.EscapeDataString(ico)}", ct).ConfigureAwait(false);
        return doc is null ? [] : RpoRecordParser.ParseSearch(doc.RootElement);
    }

    public async Task<RpoCompany?> GetCompanyAsync(long id, CancellationToken ct = default)
    {
        using var doc = await GetRpoAsync($"entity/{id}?showHistoricalData=true", ct).ConfigureAwait(false);
        return doc is null ? null : RpoRecordParser.ParseEntity(doc.RootElement);
    }

    private string Orsr => options.Value.OrsrBaseUrl.TrimEnd('/');

    private async Task<string?> GetOrsrAsync(string url, CancellationToken ct)
    {
        using var response = await SendAsync(url, "text/html", ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        var bytes = await response.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);
        return Windows1250.GetString(bytes);
    }

    private async Task<JsonDocument?> GetRpoAsync(string path, CancellationToken ct)
    {
        using var response = await SendAsync($"{options.Value.RpoBaseUrl.TrimEnd('/')}/{path}", "application/json", ct).ConfigureAwait(false);
        if (response.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.BadRequest)
        {
            return null;
        }

        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// One request at a time, each after the configured pause: the client is a singleton,
    /// so a lookup from the check page and the nightly run share one pace.
    /// </summary>
    private async Task<HttpResponseMessage> SendAsync(string url, string accept, CancellationToken ct)
    {
        await _pace.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (options.Value.DelayMilliseconds > 0)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(options.Value.DelayMilliseconds), clock, ct).ConfigureAwait(false);
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", sancMeld.Value.Ingest.UserAgent);
            request.Headers.TryAddWithoutValidation("Accept", accept);
            return await httpClients.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
        }
        finally
        {
            _pace.Release();
        }
    }

    private static string Encode(string text) =>
        string.Concat(Windows1250.GetBytes(text).Select(b =>
            (b is >= (byte)'A' and <= (byte)'Z') || (b is >= (byte)'a' and <= (byte)'z') || (b is >= (byte)'0' and <= (byte)'9') || b is (byte)'-' or (byte)'.' or (byte)'_'
                ? ((char)b).ToString()
                : $"%{b:X2}"));

    private static Encoding CreateWindows1250()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        return Encoding.GetEncoding(1250);
    }
}
