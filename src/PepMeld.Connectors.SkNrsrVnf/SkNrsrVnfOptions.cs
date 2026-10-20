using System.ComponentModel.DataAnnotations;

namespace PepMeld.Connectors.SkNrsrVnf;

/// <summary>Bound from "PepMeld:Connectors:SkNrsrVnf".</summary>
public sealed class SkNrsrVnfOptions
{
    public const string SectionName = "PepMeld:Connectors:SkNrsrVnf";

    [Required]
    public string BaseUrl { get; set; } = "https://www.nrsr.sk/web/";

    /// <summary>Pause after each request, per lane. The declaration pages take the site 1 to 20 seconds each; this is on top.</summary>
    [Range(0, 10000)]
    public int DelayMilliseconds { get; set; } = 500;

    /// <summary>
    /// Declarations fetched at once. The site needs 1 to 20 seconds per declaration page
    /// whatever the client does, so one lane would take a first fill of 1 650 pages
    /// through the night. Four lanes managed about 35 pages a minute on 2026-10-20 and
    /// were answered 429 after some 600 pages; two is the default, with the back-off
    /// below for when the site still says no. The list page is always one request, and
    /// later runs mostly read the cache.
    /// </summary>
    [Range(1, 8)]
    public int Concurrency { get; set; } = 2;

    /// <summary>
    /// Times a request answered 429 or 503 is tried again before the import fails. Every
    /// lane pauses together for what Retry-After says, or for 20 s doubling to 5 min.
    /// </summary>
    [Range(0, 20)]
    public int MaxRetries { get; set; } = 8;

    /// <summary>
    /// How long a downloaded declaration is reused. Declarations are filed once a year
    /// (by 30 April) and the page shows the latest, so a month is safe; the list of who
    /// is an official is read on every run and a newcomer's page is fetched at once.
    /// </summary>
    [Range(0, 365)]
    public int DeclarationCacheDays { get; set; } = 30;
}
