using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.Wikidata;

/// <summary>
/// Wikidata as a register of who owns, founded and runs what.
///
/// CC0, so there is nothing to license; what the data asks for is care, not permission.
/// Anyone can edit it, so every link made from it is a candidate with its evidence
/// attached, and the thing that makes a person match defensible — the exact date of
/// birth — is required, not preferred. The query service asks for a descriptive
/// User-Agent, one query at a time and a sixty-second ceiling per query, and the client
/// keeps to that.
/// </summary>
public sealed class WikidataOptions
{
    public const string SectionName = "SancMeld:Wikidata";

    /// <summary>Off means no scheduled job and no query; the stored links still serve.</summary>
    public bool Enabled { get; set; } = true;

    [Required]
    public string SparqlEndpoint { get; set; } = "https://query.wikidata.org/sparql";

    /// <summary>When the scheduled run fires. Nightly, after the list imports.</summary>
    [Required]
    public string CronExpression { get; set; } = "30 2 * * *";

    /// <summary>
    /// How alike a listed spelling and a register label must be, as pg_trgm similarity,
    /// once the birth date already agrees. 0.6 is well above a surname alone and below
    /// the patronymic-or-not variation of the same person.
    /// </summary>
    [Range(0.3, 1.0)]
    public double MinNameScore { get; set; } = 0.6;

    /// <summary>
    /// Two register records born the same day whose names score within this of each
    /// other are not told apart; the person is left unresolved with a note rather than
    /// guessed.
    /// </summary>
    [Range(0.0, 0.5)]
    public double AmbiguityMargin { get; set; } = 0.05;

    /// <summary>
    /// A person the register did not have is looked up again after this long. The register
    /// grows; a month is often enough for a newly listed person to get an item.
    /// </summary>
    [Range(1, 365)]
    public int RetryMissAfterDays { get; set; } = 30;

    /// <summary>How often a resolved person's relations are re-read from the register.</summary>
    [Range(1, 365)]
    public int RefreshRelationsAfterDays { get; set; } = 7;

    /// <summary>
    /// Birth dates looked up per scheduled run. One query per date at two to five
    /// seconds each (measured 2026-10-16), so 300 is under half an hour a night, and the
    /// 4 270 dates on the lists on that day drain in a fortnight. The command line has
    /// no such cap: the first fill is meant to be run by hand, detached.
    /// </summary>
    [Range(1, 100_000)]
    public int MaxDatesPerScheduledRun { get; set; } = 300;

    /// <summary>Pause between two queries, so a long run is one steady client rather than a burst.</summary>
    [Range(0, 60_000)]
    public int QueryDelayMs { get; set; } = 500;

    /// <summary>
    /// Birth dates resolved before the relations of the persons found so far are read.
    /// The first fill is hours of birth-date queries; reading relations only at the end
    /// meant a run cut short — a deploy, a dropped connection — kept thousands of resolved
    /// persons and not one relation.
    /// </summary>
    [Range(1, 100_000)]
    public int RelationsEveryDates { get; set; } = 200;

    /// <summary>Resolved persons whose relations are read in one query.</summary>
    [Range(1, 200)]
    public int RelationBatchSize { get; set; } = 50;

    /// <summary>Statements looked up in one query once both ends are known.</summary>
    [Range(1, 500)]
    public int StatementBatchSize { get; set; } = 100;

    /// <summary>
    /// Relations read per register item. A head of state chairs and founded hundreds of
    /// things; past this many the item is a public figure, not an owner, and the rest is
    /// left unread with a line in the log.
    /// </summary>
    [Range(1, 10_000)]
    public int MaxRelationsPerItem { get; set; } = 200;

    /// <summary>Consecutive failed queries after which a run gives up — the service is down, not the query wrong.</summary>
    [Range(1, 100)]
    public int MaxConsecutiveFailures { get; set; } = 5;

    [Range(10, 600)]
    public int TimeoutSeconds { get; set; } = 90;
}
