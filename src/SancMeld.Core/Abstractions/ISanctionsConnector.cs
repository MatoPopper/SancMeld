namespace SancMeld.Core.Abstractions;

/// <summary>
/// One connector per sanctions source.
///
/// Discovery is deliberately separate from download: government attachment
/// URLs change between releases (Poland ships a GUID attachment, Czechia a
/// dated PDF filename), so resolving "where is the current file" is its own
/// step that must be able to fail independently of parsing.
/// </summary>
public interface ISanctionsConnector
{
    /// <summary>Stable source code, e.g. "EU_FSF". Matches sanctions_source.code.</summary>
    string SourceCode { get; }

    /// <summary>Resolve the current download location from the landing page or catalogue.</summary>
    Task<SourceDiscoveryResult> DiscoverAsync(CancellationToken ct);

    /// <summary>
    /// Download the artefact. Implementations must send a User-Agent header —
    /// OFAC's Sanctions List Service returns 403 without one.
    /// </summary>
    Task<DownloadResult> DownloadAsync(
        SourceDiscoveryResult discovery,
        ConditionalRequestState conditional,
        CancellationToken ct);

    /// <summary>
    /// Stream records. Must not load the whole document into memory:
    /// the EU XML is ~26 MB and OFAC's advanced XML ~126 MB.
    /// </summary>
    IAsyncEnumerable<ParsedDesignation> ParseAsync(
        Stream source,
        SourceSnapshotMetadata metadata,
        CancellationToken ct);

    /// <summary>
    /// Sanity-check the parsed dataset BEFORE anything is published.
    /// A truncated download must never be allowed to empty the live list.
    /// </summary>
    Task<DatasetValidationResult> ValidateAsync(ParsedDatasetStatistics stats, CancellationToken ct);
}

public sealed record SourceDiscoveryResult(
    Uri DownloadUrl,
    string? ExternalVersion,
    IReadOnlyDictionary<string, string>? Headers = null);

public sealed record ConditionalRequestState(string? ETag, string? LastModified, string? PreviousSha256)
{
    /// <summary>
    /// <see cref="SourceDiscoveryResult.ExternalVersion"/> from the previous successful
    /// import.
    ///
    /// Not every publisher honours conditional requests. EU FSF answers
    /// If-Modified-Since with 200 and the full 25 MB (verified 2026-08-06), so the only
    /// cheap way to tell whether anything changed is the version its catalogue
    /// advertises. A connector that has one compares it here and skips the download.
    /// </summary>
    public string? PreviousExternalVersion { get; init; }
}

public sealed record DownloadResult(
    Stream Content,
    int HttpStatus,
    string Sha256,
    long ByteSize,
    string? ContentType,
    string? ETag,
    string? LastModified)
{
    /// <summary>True when the payload is byte-identical to the previous run; skip parsing entirely.</summary>
    public bool Unchanged { get; init; }
}

public sealed record SourceSnapshotMetadata(
    Guid SnapshotId,
    string SourceCode,
    string Sha256,
    string? ExternalVersion,
    DateTimeOffset RetrievedAt);

public sealed record ParsedDatasetStatistics(int RecordCount, int ParseErrorCount, int? PreviousRecordCount);

public sealed record DatasetValidationResult(bool IsAcceptable, string? Reason)
{
    public static DatasetValidationResult Ok() => new(true, null);
    public static DatasetValidationResult Reject(string reason) => new(false, reason);
}
