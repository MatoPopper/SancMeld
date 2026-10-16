using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.Rpvs;

/// <summary>
/// RPVS, the Slovak register of public sector partners, read for the beneficial owners of
/// every company that does business with the Slovak state. Its open-data service lists
/// each owner with a full date of birth, which is what makes it usable: a sanctions
/// match on a name alone would be a guess.
///
/// Unlike ARES it is downloaded whole, about sixty thousand partners in pages of twenty,
/// and compared here. Nothing a user screens is ever sent to it, and only the owners who
/// turn out to be listed are kept; everyone else is read, compared and forgotten.
/// </summary>
public sealed class RpvsOptions
{
    public const string SectionName = "SancMeld:Rpvs";

    /// <summary>Off means no call to RPVS and no scheduled job; stored links keep serving.</summary>
    public bool Enabled { get; set; } = true;

    [Required]
    public string BaseUrl { get; set; } = "https://rpvs.gov.sk/opendatav2";

    /// <summary>The public page of a partner, for the evidence link; {0} is the partner id.</summary>
    [Required]
    public string PartnerPageUrl { get; set; } = "https://rpvs.gov.sk/rpvs/Partner/Partner/Detail/{0}";

    /// <summary>Weekly: the register changes by a few hundred partners a week.</summary>
    [Required]
    public string CronExpression { get; set; } = "30 4 * * 0";

    /// <summary>Pause between pages, so three thousand requests are not a burst.</summary>
    [Range(0, 10_000)]
    public int DelayMilliseconds { get; set; } = 100;

    /// <summary>Stops after this many pages; 0 reads the whole register. For trying it out.</summary>
    [Range(0, int.MaxValue)]
    public int MaxPages { get; set; }

    [Range(0.3, 1.0)]
    public double MinPersonNameScore { get; set; } = 0.6;

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 60;
}
