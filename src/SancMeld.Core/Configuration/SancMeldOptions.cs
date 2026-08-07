using System.ComponentModel.DataAnnotations;

namespace SancMeld.Core.Configuration;

/// <summary>
/// Configuration bound from the "SancMeld" section, validated at startup.
///
/// The connection string is deliberately NOT part of this class: it lives under
/// the standard "ConnectionStrings:SancMeld" key so it can be supplied by user
/// secrets or an environment variable without ever touching a tracked file.
/// </summary>
public sealed class SancMeldOptions
{
    public const string SectionName = "SancMeld";

    public IngestOptions Ingest { get; set; } = new();
    public MatchingOptions Matching { get; set; } = new();
}

public sealed class IngestOptions
{
    /// <summary>
    /// Where raw downloaded artefacts are kept. Every import stores the file it
    /// actually received, so a past decision can be reconstructed and a parser
    /// bug can be fixed retroactively without re-downloading.
    /// </summary>
    [Required]
    public string SnapshotStoragePath { get; set; } = "./.snapshots";

    /// <summary>
    /// Sent with every download. Not optional: OFAC's Sanctions List Service
    /// returns HTTP 403 to requests without a User-Agent, and .NET's HttpClient
    /// sends none by default.
    /// </summary>
    [Required, MinLength(3)]
    public string UserAgent { get; set; } = "SancMeld/1.0 (+https://github.com/MatoPopper/SancMeld)";

    [Range(1, 3600)]
    public int DownloadTimeoutSeconds { get; set; } = 600;

    [Range(0, 10)]
    public int DownloadRetryCount { get; set; } = 3;
}

public sealed class MatchingOptions
{
    /// <summary>
    /// pg_trgm similarity threshold for candidate generation.
    ///
    /// Lower = wider net = higher recall = more analyst work. In sanctions
    /// screening a missed match is a legal breach while a false positive only
    /// costs time, so err low. Measured against the full EU list, 0.30-0.35
    /// reaches 100% recall@10.
    /// </summary>
    [Range(0.0, 1.0)]
    public double SimilarityThreshold { get; set; } = 0.35;

    [Range(1, 1000)]
    public int MaxCandidates { get; set; } = 100;

    /// <summary>
    /// Score at or above which a candidate is presented as a hit.
    ///
    /// A different question from SimilarityThreshold, and the two must not be conflated:
    /// that one decides what the database retrieves, this one decides what an analyst is
    /// asked to look at. Candidates below it are still recorded — they are the negative
    /// half of any future training set, and a system that never stores them can only ever
    /// learn from what it already shows.
    /// </summary>
    [Range(0.0, 1.0)]
    public double MatchThreshold { get; set; } = 0.50;

    /// <summary>
    /// Whether generic aliases ("Abu Ahmad") take part in primary scoring.
    /// Off by default, matching OFAC's own guidance: weak aliases are meant to
    /// corroborate a hit raised by other evidence, not to raise one.
    /// </summary>
    public bool ScoreWeakAliases { get; set; }
}
