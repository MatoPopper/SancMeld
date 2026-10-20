using System.ComponentModel.DataAnnotations;

namespace PepMeld.Connectors.SkNrsr;

/// <summary>Bound from "PepMeld:Connectors:SkNrsr".</summary>
public sealed class SkNrsrOptions
{
    public const string SectionName = "PepMeld:Connectors:SkNrsr";

    [Required]
    public string BaseUrl { get; set; } = "https://www.nrsr.sk/web/";

    /// <summary>
    /// Which terms to read, as "3-9", "9" or "7,8,9". The site offers the 3rd term (2002)
    /// onwards. A term left out here is not "ended": its members simply vanish from the
    /// file and their functions are closed by disappearance, so narrow this only on a
    /// fresh database.
    /// </summary>
    [Required]
    public string Terms { get; set; } = "3-9";

    /// <summary>Pause between two requests. The site is a public service, not an API; 250 ms keeps a full run under five minutes.</summary>
    [Range(0, 10000)]
    public int DelayMilliseconds { get; set; } = 250;

    /// <summary>
    /// How long a downloaded profile is reused before it is fetched again. A profile
    /// holds the identity (name, date of birth, residence), which does not change;
    /// which terms a person sat in comes from the lists, read on every run. With
    /// 30 days a daily run makes about 160 requests instead of 700.
    /// </summary>
    [Range(0, 365)]
    public int ProfileCacheDays { get; set; } = 30;
}
