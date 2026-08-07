namespace SancMeld.Core.Ingest;

/// <summary>What one import run did. Mirrors the columns kept on import_run.</summary>
public sealed record ImportOutcome
{
    public required Guid ImportRunId { get; init; }
    public required string SourceCode { get; init; }
    public required ImportStatus Status { get; init; }

    public int RecordsSeen { get; init; }
    public int Inserted { get; init; }
    public int Updated { get; init; }
    public int Unchanged { get; init; }

    /// <summary>Records the publisher stopped sending. Marked, never deleted.</summary>
    public int MarkedPossiblyMissing { get; init; }

    public int Reappeared { get; init; }
    public string? Reason { get; init; }
    public TimeSpan Duration { get; init; }
}

public enum ImportStatus
{
    Success,

    /// <summary>The publisher is still serving the release we already hold.</summary>
    SkippedUnchanged,

    /// <summary>The dataset failed its sanity thresholds. Nothing was published.</summary>
    RejectedValidation,

    Failed,
}
