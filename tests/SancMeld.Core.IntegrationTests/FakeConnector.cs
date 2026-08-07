using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SancMeld.Core.Abstractions;
using SancMeld.Core.Domain;

namespace SancMeld.Core.IntegrationTests;

/// <summary>
/// Stands in for a publisher, so the diff can be tested against releases that are chosen
/// rather than waited for. It behaves like a real connector in the ways the pipeline
/// depends on: the version marker and the payload hash both follow the content, which is
/// what makes "unchanged" detectable.
/// </summary>
internal sealed class FakeConnector(string sourceCode) : ISanctionsConnector
{
    public string SourceCode { get; } = sourceCode;

    public List<ParsedDesignation> Records { get; set; } = [];

    /// <summary>Set to reject the next dataset, as a connector's own guards would.</summary>
    public string? RejectWith { get; set; }

    public int DownloadCount { get; private set; }

    public Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct) =>
        Task.FromResult(new SourceDiscoveryResult(
            new Uri($"https://example.invalid/{SourceCode}"), Version()));

    public Task<DownloadResult> DownloadAsync(
        SourceDiscoveryResult discovery, ConditionalRequestState conditional, CancellationToken ct)
    {
        if (discovery.ExternalVersion == conditional.PreviousExternalVersion)
        {
            return Task.FromResult(new DownloadResult(
                Stream.Null, 0, conditional.PreviousSha256 ?? string.Empty, 0, null, null, null)
            {
                Unchanged = true,
            });
        }

        DownloadCount++;
        var bytes = Encoding.UTF8.GetBytes(Serialized());

        return Task.FromResult(new DownloadResult(
            new MemoryStream(bytes), 200, Version(), bytes.Length, "application/json", null, null));
    }

    public async IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        SourceSnapshotMetadata metadata,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();

        foreach (var record in Records)
        {
            yield return record;
        }
    }

    public Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct) =>
        Task.FromResult(RejectWith is null
            ? DatasetValidationResult.Ok()
            : DatasetValidationResult.Reject(RejectWith));

    private string Version() =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Serialized()))).ToLowerInvariant();

    private string Serialized() => JsonSerializer.Serialize(Records);
}
