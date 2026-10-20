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
    public int DelayMilliseconds { get; set; } = 250;

    /// <summary>
    /// Declarations fetched at once. The site needs 1 to 20 seconds per declaration page
    /// whatever the client does, so one lane would take a first fill of 1 650 pages
    /// through the night; four lanes bring it to about two hours. The list page is
    /// always one request, and later runs mostly read the cache.
    /// </summary>
    [Range(1, 8)]
    public int Concurrency { get; set; } = 4;

    /// <summary>
    /// How long a downloaded declaration is reused. Declarations are filed once a year
    /// (by 30 April) and the page shows the latest, so a month is safe; the list of who
    /// is an official is read on every run and a newcomer's page is fetched at once.
    /// </summary>
    [Range(0, 365)]
    public int DeclarationCacheDays { get; set; } = 30;
}
