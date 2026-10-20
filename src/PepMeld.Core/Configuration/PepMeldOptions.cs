using System.ComponentModel.DataAnnotations;

namespace PepMeld.Core.Configuration;

/// <summary>
/// Bound from the "PepMeld" section and validated at startup. The connection string is
/// not here: it lives under "ConnectionStrings:PepMeld" so user secrets or an environment
/// variable can supply it without touching a tracked file.
/// </summary>
public sealed class PepMeldOptions
{
    public const string SectionName = "PepMeld";

    public IngestOptions Ingest { get; set; } = new();
    public MatchingOptions Matching { get; set; } = new();
    public ChecksOptions Checks { get; set; } = new();
}

public sealed class IngestOptions
{
    /// <summary>Where downloaded files are kept. Mount a volume here in Docker or they vanish with the container.</summary>
    [Required]
    public string SnapshotStoragePath { get; set; } = "./.snapshots";

    /// <summary>Sent with every download. Publishers want to know who reads them once a day; some refuse an anonymous client outright.</summary>
    [Required]
    public string UserAgent { get; set; } = "PepMeld/0.1 (+https://github.com/MatoPopper/SancMeld)";

    [Range(10, 3600)]
    public int DownloadTimeoutSeconds { get; set; } = 300;
}

public sealed class MatchingOptions
{
    /// <summary>
    /// pg_trgm similarity below which a stored name is not even a candidate. 0.6 rather
    /// than SancMeld's 0.35: Slovak and Czech names arrive in one script with at most a
    /// title, an accent or an "-ová" between the query and the list, and a wider net
    /// would mostly catch namesakes the date of birth then has to throw out.
    /// </summary>
    [Range(0.1, 1.0)]
    public double SimilarityThreshold { get; set; } = 0.6;

    /// <summary>
    /// What a query scores when every one of its words is in the stored name: "Petr
    /// Fiala" against "Petr Pavel Fiala". Taken from SancMeld's RegisterPersonMatcher,
    /// where the same pair, a name with and without a middle name, is the most common
    /// difference between a list and a register.
    /// </summary>
    [Range(0.1, 1.0)]
    public double ContainmentScore { get; set; } = 0.9;

    [Range(1, 500)]
    public int MaxCandidates { get; set; } = 50;

    /// <summary>
    /// How long after a function ends the person is reported as a former PEP with
    /// emphasis. SK § 12 ods. 3 says at least twelve months; the finding carries the
    /// months regardless, and this only decides the wording.
    /// </summary>
    [Range(1, 120)]
    public int FormerPepMonths { get; set; } = 12;
}

public sealed class ChecksOptions
{
    /// <summary>Checks run at once. Each is a few database queries today; the family stage will make this matter.</summary>
    [Range(1, 64)]
    public int MaxParallel { get; set; } = 4;
}
