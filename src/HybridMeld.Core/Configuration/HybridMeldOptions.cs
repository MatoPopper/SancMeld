using System.ComponentModel.DataAnnotations;

namespace HybridMeld.Core.Configuration;

/// <summary>
/// Bound from the "HybridMeld" section and validated at startup. Connection strings are
/// not here: "ConnectionStrings:HybridMeld" is the workspace's own database,
/// "ConnectionStrings:SancMeldAccounts" the sanctions database it reads the grant, the
/// regimes and the link targets from, and "ConnectionStrings:PepMeld" the PEP database
/// it checks person links against. The last two are optional on a development host.
/// </summary>
public sealed class HybridMeldOptions
{
    public const string SectionName = "HybridMeld";

    public IngestOptions Ingest { get; set; } = new();
    public LinksOptions Links { get; set; } = new();
}

public sealed class IngestOptions
{
    /// <summary>Where fetched pages and posts are kept. Mount a volume here in Docker or they vanish with the container.</summary>
    [Required]
    public string SnapshotStoragePath { get; set; } = "./.snapshots";

    [Required]
    public string UserAgent { get; set; } = "HybridMeld/0.1 (+https://github.com/MatoPopper/SancMeld)";

    [Range(5, 600)]
    public int FetchTimeoutSeconds { get; set; } = 30;

    /// <summary>Posts a channel fetch keeps per run; the public Telegram page shows about twenty, a feed rarely more than fifty.</summary>
    [Range(1, 500)]
    public int MaxItemsPerFetch { get; set; } = 100;

    /// <summary>Characters of text kept on the item row; the snapshot holds the rest.</summary>
    [Range(200, 100_000)]
    public int MaxTextLength { get; set; } = 8000;
}

public sealed class LinksOptions
{
    /// <summary>Front-end path of a sanctions record, with {id}; the graph sends readers there and never the other way.</summary>
    [Required]
    public string SanctionRoute { get; set; } = "/designations/{id}";

    /// <summary>Front-end path of a PEP record. PepMeld has no detail page yet, so a PEP check by name is the nearest thing.</summary>
    [Required]
    public string PepRoute { get; set; } = "/pep?name={name}";

    /// <summary>How old a verification may be before the job checks the link again.</summary>
    [Range(1, 24 * 90)]
    public int VerifyAfterHours { get; set; } = 24 * 7;
}
