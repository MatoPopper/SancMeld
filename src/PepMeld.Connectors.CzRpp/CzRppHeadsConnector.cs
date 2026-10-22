using Microsoft.Extensions.Options;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.CzRpp;

/// <summary>
/// The heads of Czech regions, statutory cities, municipalities with extended powers,
/// Prague's districts 1 to 22, ministries and the other central offices, from the register
/// of public authorities. Official and daily, but without a date of birth: a match is a
/// level C candidate until another list lends the date (see BirthDateFill).
/// </summary>
public sealed class CzRppHeadsConnector(IHttpClientFactory httpClients, IOptions<CzRppOptions> options) : IPepListConnector
{
    public const string Code = "CZ_RPP_OVM";
    public const string HttpClientName = "PepMeld.CzRpp";

    public string SourceCode => Code;

    public async Task<ListDownload> DownloadAsync(CancellationToken ct)
    {
        var client = httpClients.CreateClient(HttpClientName);
        var url = options.Value.Url;

        var response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

        try
        {
            response.EnsureSuccessStatusCode();
            var content = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            return new ListDownload(content, url, response);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    public IReadOnlyList<ParsedPerson> Parse(Stream content)
    {
        ArgumentNullException.ThrowIfNull(content);
        return OvmMapper.Map(content);
    }
}
