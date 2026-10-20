using System.IO.Compression;
using Microsoft.Extensions.Options;
using PepMeld.Core.Lists;

namespace PepMeld.Connectors.CzPsp;

/// <summary>
/// The Chamber of Deputies' open data: every deputy since 1993 with the date of birth,
/// plus senators, members of government and presidents, each with the dates of the
/// mandate. The best source in the product: official, daily, machine-readable, and with
/// the one field most official lists leave out.
/// </summary>
public sealed class CzPspConnector(IHttpClientFactory httpClients, IOptions<CzPspOptions> options) : IPepListConnector
{
    public const string Code = "CZ_PSP";
    public const string HttpClientName = "PepMeld.CzPsp";

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

        using var archive = new ZipArchive(content, ZipArchiveMode.Read, leaveOpen: true);
        return PspMapper.Map(archive);
    }
}
