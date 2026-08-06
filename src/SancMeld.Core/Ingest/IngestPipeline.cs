namespace SancMeld.Core.Ingest;

/// <summary>
/// The fixed order every import follows. Each phase can fail independently and
/// the run is abandoned without touching published data.
///
/// The critical property: nothing is published until Validate has passed. A
/// truncated or error-page download must never be able to empty the live list.
/// </summary>
public enum IngestPhase
{
    /// <summary>Resolve the current download URL (attachment GUIDs and filenames change).</summary>
    DiscoverEndpoint = 1,

    /// <summary>Conditional GET with User-Agent, retry and timeout.</summary>
    Download = 2,

    /// <summary>HTTP status, content type, size. Catches login and error pages served as 200.</summary>
    ValidateTransport = 3,

    /// <summary>Persist the raw artefact plus its sha256 before anything is interpreted.</summary>
    StoreSnapshot = 4,

    /// <summary>Streamed parse into ParsedDesignation.</summary>
    Parse = 5,

    /// <summary>Sanity thresholds: minimum record count, maximum parse-error rate.</summary>
    ValidateDataset = 6,

    /// <summary>Write into staging, still invisible to queries.</summary>
    Stage = 7,

    /// <summary>Compare against the current revision: added / changed / removed.</summary>
    Diff = 8,

    /// <summary>Close changed revisions, open new ones. Atomic.</summary>
    Commit = 9,

    /// <summary>Apply cross-references and probabilistic links.</summary>
    ResolveEntities = 10,

    /// <summary>Structured record of what happened, for the audit trail.</summary>
    EmitAuditEvent = 11
}

/// <summary>
/// Guards applied at <see cref="IngestPhase.ValidateDataset"/>.
/// Defaults are deliberately conservative: refusing an import is cheap,
/// publishing an empty sanctions list is not.
/// </summary>
public sealed record IngestGuards
{
    /// <summary>Reject if fewer records than the source's configured minimum.</summary>
    public int? MinimumRecordCount { get; init; }

    /// <summary>Reject if the record count drops by more than this fraction versus the previous run.</summary>
    public double MaxShrinkFraction { get; init; } = 0.20;

    /// <summary>Reject if more than this fraction of records failed to parse.</summary>
    public double MaxParseErrorFraction { get; init; } = 0.05;

    /// <summary>
    /// Absence from one successful import marks a designation PossiblyMissing,
    /// never Delisted. Mass delisting requires an explicit, reviewed decision.
    /// </summary>
    public bool AllowAutomaticDelisting { get; init; } = false;
}
