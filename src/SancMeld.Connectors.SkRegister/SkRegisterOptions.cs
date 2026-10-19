using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.SkRegister;

/// <summary>
/// The Slovak business register, asked who in Slovakia carries a listed name.
///
/// Two services, because neither does the whole job. ORSR (orsr.sk, the courts' business
/// register) is the only one that finds a <em>person</em> by name — the shareholders and
/// directors of every company — but it answers HTML pages and publishes no address or date
/// of birth for a person. RPO (api.statistics.sk, the Statistical Office's register of
/// legal persons) answers JSON with the address and country of every shareholder and
/// director, but is searched only by company. ORSR finds the companies; RPO says who in
/// them is who.
///
/// No register here publishes a date of birth, so everything this connector links is a
/// name match: a lead, kept as additional information (<see cref="Core.Relations.RelationSystems.SkRegister"/>)
/// and shown only to the users allowed to see it.
/// </summary>
public sealed class SkRegisterOptions
{
    public const string SectionName = "SancMeld:SkRegister";

    /// <summary>Off means no request to ORSR or RPO and no scheduled job; stored links keep serving.</summary>
    public bool Enabled { get; set; } = true;

    [Required]
    public string OrsrBaseUrl { get; set; } = "https://www.orsr.sk/";

    [Required]
    public string RpoBaseUrl { get; set; } = "https://api.statistics.sk/rpo/v1/";

    /// <summary>
    /// Pause before every request. ORSR is a court's public website, not an API, and the
    /// pace is the one a person clicking through it would keep.
    /// </summary>
    [Range(0, 60_000)]
    public int DelayMilliseconds { get; set; } = 1500;

    /// <summary>Nightly, after the imports: the listings a run did not get to wait a day.</summary>
    [Required]
    public string CronExpression { get; set; } = "15 3 * * *";

    /// <summary>
    /// Listings looked up per scheduled run, newest first. A listing costs three to ten
    /// requests; the 22nd package went at about 13 listings a minute, so 800 — a whole
    /// package (the 22nd had 769) — is about an hour, done before the EU import at 04:00 UTC.
    /// </summary>
    [Range(0, 10_000)]
    public int MaxListingsPerScheduledRun { get; set; } = 800;

    /// <summary>
    /// Listings an import may look up on the spot. A day's additions are a handful; a new
    /// sanctions package is eight hundred and goes to the nightly runs instead, so the
    /// import does not wait an hour for a register.
    /// </summary>
    [Range(0, 1_000)]
    public int MaxListingsInline { get; set; } = 30;

    /// <summary>Result pages of 20 read for one surname before narrowing by given name.</summary>
    [Range(1, 10)]
    public int MaxPersonPages { get; set; } = 2;

    /// <summary>Companies read for one listing; a common name stops here and says so.</summary>
    [Range(1, 100)]
    public int MaxCompaniesPerListing { get; set; } = 12;

    [Range(0.3, 1.0)]
    public double MinPersonNameScore { get; set; } = 0.6;

    /// <summary>A listing nothing was found for is asked again after this long.</summary>
    [Range(1, 3650)]
    public int RetryAfterDays { get; set; } = 90;

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 40;
}
