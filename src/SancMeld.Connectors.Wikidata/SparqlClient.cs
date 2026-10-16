using System.Globalization;
using System.Net;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;

namespace SancMeld.Connectors.Wikidata;

/// <summary>One SPARQL SELECT, answered as rows. The seam the tests replace.</summary>
public interface ISparqlClient
{
    Task<IReadOnlyList<SparqlRow>> QueryAsync(string sparql, CancellationToken ct = default);
}

/// <summary>One solution of a SELECT: the bound variables, by name.</summary>
public sealed class SparqlRow(IReadOnlyDictionary<string, SparqlValue> values)
{
    public SparqlValue? Get(string variable) => values.GetValueOrDefault(variable);

    public string? this[string variable] => Get(variable)?.Value;

    /// <summary>The Q- or P-number when the variable is a Wikidata entity, else null.</summary>
    public string? EntityId(string variable) => Get(variable)?.EntityId;
}

public sealed record SparqlValue(string Value, string Type, string? Lang, string? Datatype)
{
    private const string EntityPrefix = "http://www.wikidata.org/entity/";

    public string? EntityId =>
        Type == "uri" && Value.StartsWith(EntityPrefix, StringComparison.Ordinal)
            ? Value[EntityPrefix.Length..]
            : null;

    /// <summary>A date from a dateTime literal. Dates the register knows only to the year still come back as the first of January; callers that care read the precision themselves.</summary>
    public DateOnly? Date =>
        Value.Length >= 10 && DateOnly.TryParseExact(Value[..10], "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d)
            ? d
            : null;

    public decimal? Decimal =>
        decimal.TryParse(Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : null;

    public bool IsTrue => string.Equals(Value, "true", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// The Wikidata Query Service, called the way its usage policy asks: a descriptive
/// User-Agent, one query at a time, a pause between queries, and a retry only after the
/// wait the service names in Retry-After.
/// </summary>
public sealed class WikidataSparqlClient(
    IHttpClientFactory httpClients,
    IOptions<WikidataOptions> options,
    IOptions<SancMeldOptions> sancMeld,
    TimeProvider clock,
    ILogger<WikidataSparqlClient> logger) : ISparqlClient
{
    public const string HttpClientName = "SancMeld.Wikidata";

    private readonly SemaphoreSlim _oneAtATime = new(1, 1);
    private long _lastQueryAt;

    public async Task<IReadOnlyList<SparqlRow>> QueryAsync(string sparql, CancellationToken ct = default)
    {
        var settings = options.Value;

        await _oneAtATime.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            await PauseAsync(settings, ct).ConfigureAwait(false);

            for (var attempt = 1; ; attempt++)
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.SparqlEndpoint)
                {
                    Content = new FormUrlEncodedContent([new KeyValuePair<string, string>("query", sparql)]),
                };
                request.Headers.TryAddWithoutValidation("User-Agent", sancMeld.Value.Ingest.UserAgent);
                request.Headers.TryAddWithoutValidation("Accept", "application/sparql-results+json");

                var http = httpClients.CreateClient(HttpClientName);
                using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                _lastQueryAt = clock.GetTimestamp();

                if (response.IsSuccessStatusCode)
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                    return await ParseAsync(stream, ct).ConfigureAwait(false);
                }

                var retryable = response.StatusCode is HttpStatusCode.TooManyRequests
                    or HttpStatusCode.ServiceUnavailable or HttpStatusCode.BadGateway or HttpStatusCode.GatewayTimeout
                    or HttpStatusCode.InternalServerError;

                if (!retryable || attempt >= 3)
                {
                    var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
                    throw new HttpRequestException(
                        $"Wikidata Query Service answered {(int)response.StatusCode} on attempt {attempt}: {Head(body)}",
                        null, response.StatusCode);
                }

                // 429 names the wait; otherwise back off by attempt. Never hammer a
                // service that has just said it is busy.
                var wait = response.Headers.RetryAfter?.Delta ?? TimeSpan.FromSeconds(10 * attempt);
                logger.LogWarning("Wikidata answered {Status}; waiting {Wait} before attempt {Attempt}.", (int)response.StatusCode, wait, attempt + 1);
                await Task.Delay(wait, clock, ct).ConfigureAwait(false);
            }
        }
        finally
        {
            _oneAtATime.Release();
        }
    }

    private async Task PauseAsync(WikidataOptions settings, CancellationToken ct)
    {
        if (_lastQueryAt == 0 || settings.QueryDelayMs == 0)
        {
            return;
        }

        var since = clock.GetElapsedTime(_lastQueryAt);
        var remaining = TimeSpan.FromMilliseconds(settings.QueryDelayMs) - since;

        if (remaining > TimeSpan.Zero)
        {
            await Task.Delay(remaining, clock, ct).ConfigureAwait(false);
        }
    }

    /// <summary>The SPARQL 1.1 JSON results format: head.vars and results.bindings.</summary>
    internal static async Task<IReadOnlyList<SparqlRow>> ParseAsync(Stream json, CancellationToken ct)
    {
        using var document = await JsonDocument.ParseAsync(json, cancellationToken: ct).ConfigureAwait(false);

        if (!document.RootElement.TryGetProperty("results", out var results)
            || !results.TryGetProperty("bindings", out var bindings))
        {
            throw new InvalidDataException("The response is not a SPARQL JSON result: no results.bindings.");
        }

        var rows = new List<SparqlRow>();

        foreach (var binding in bindings.EnumerateArray())
        {
            var values = new Dictionary<string, SparqlValue>(StringComparer.Ordinal);

            foreach (var property in binding.EnumerateObject())
            {
                var v = property.Value;
                values[property.Name] = new SparqlValue(
                    v.GetProperty("value").GetString() ?? string.Empty,
                    v.GetProperty("type").GetString() ?? "literal",
                    v.TryGetProperty("xml:lang", out var lang) ? lang.GetString() : null,
                    v.TryGetProperty("datatype", out var datatype) ? datatype.GetString() : null);
            }

            rows.Add(new SparqlRow(values));
        }

        return rows;
    }

    private static string Head(string body) => body.Length <= 200 ? body : body[..200] + "…";
}
