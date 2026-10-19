using System.Globalization;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.CzRegister;

/// <summary>The one justice.cz call this connector makes. The seam the tests replace.</summary>
public interface ICzRegisterClient
{
    /// <summary>
    /// The register's person search: by surname, optionally narrowed by given name and
    /// date of birth. Current and past roles alike (jenPlatne=VSECHNY): a listed person
    /// who left a board last year is still a lead.
    /// </summary>
    Task<JusticeSearchPage> SearchPersonAsync(string surname, string? givenName, DateOnly? birthDate, CancellationToken ct = default);
}

public sealed class CzRegisterClient(IHttpClientFactory httpClients, IOptions<CzRegisterOptions> options, IOptions<SancMeldOptions> sancMeld, TimeProvider clock)
    : ICzRegisterClient
{
    public const string HttpClientName = "SancMeld.CzRegister";

    /// <summary>Tries per request: a register that times out once usually answers the next time.</summary>
    private const int Attempts = 3;

    private readonly SemaphoreSlim _pace = new(1, 1);

    public async Task<JusticeSearchPage> SearchPersonAsync(string surname, string? givenName, DateOnly? birthDate, CancellationToken ct = default)
    {
        var url = $"{options.Value.BaseUrl.TrimEnd('/')}/rejstrik-$osoba?p::vyhledavani=true"
            + $"&prijmeni={Uri.EscapeDataString(surname)}"
            + $"&jmeno={Uri.EscapeDataString(givenName ?? string.Empty)}"
            + $"&narozeni={(birthDate is { } b ? b.ToString("dd.MM.yyyy", CultureInfo.InvariantCulture) : string.Empty)}"
            + $"&obec=&angazma=&soud=&polozek={options.Value.PageSize}&jenPlatne=VSECHNY";

        await _pace.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            for (var attempt = 1; ; attempt++)
            {
                var pause = options.Value.DelayMilliseconds * (attempt == 1 ? 1 : 4);
                if (pause > 0)
                {
                    await Task.Delay(TimeSpan.FromMilliseconds(pause), clock, ct).ConfigureAwait(false);
                }

                using var request = new HttpRequestMessage(HttpMethod.Get, url);
                request.Headers.TryAddWithoutValidation("User-Agent", sancMeld.Value.Ingest.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "text/html");

                try
                {
                    using var response = await httpClients.CreateClient(HttpClientName).SendAsync(request, ct).ConfigureAwait(false);
                    if ((int)response.StatusCode >= 500 && attempt < Attempts)
                    {
                        continue;
                    }

                    response.EnsureSuccessStatusCode();
                    var html = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    return JusticeHtmlParser.Parse(html);
                }
                catch (Exception ex) when (attempt < Attempts && !ct.IsCancellationRequested
                    && ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    // Tried again above.
                }
            }
        }
        finally
        {
            _pace.Release();
        }
    }
}
