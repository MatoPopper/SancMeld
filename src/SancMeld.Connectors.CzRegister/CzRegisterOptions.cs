using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.CzRegister;

/// <summary>
/// The Czech public register (or.justice.cz), asked who in Czechia carries a listed name.
///
/// Unlike the Slovak one it answers the question properly: its person search takes a
/// surname, a given name and a <em>date of birth</em>, and every row carries the person's
/// date of birth, address with country, role and the company's IČO. So a listed person
/// with a known birthday is either found or ruled out; the company is then read through
/// ARES (the existing connector), which links the person by name and date of birth. Only
/// a listing without a date of birth ends up as a name-only lead, kept as additional
/// information under <see cref="Core.Relations.RelationSystems.CzRegister"/>.
///
/// Terms of the register (Podmínky provozu): at most 50 requests a minute and 3 000 a
/// day per client, and a CAPTCHA may appear at random. The pace below keeps well under
/// both; a page that turns out to be a CAPTCHA is reported as "no answer" and tried
/// again another day.
/// </summary>
public sealed class CzRegisterOptions
{
    public const string SectionName = "SancMeld:CzRegister";

    /// <summary>Off means no request to justice.cz and no scheduled job; stored links keep serving.</summary>
    public bool Enabled { get; set; } = true;

    [Required]
    public string BaseUrl { get; set; } = "https://or.justice.cz/ias/ui/";

    /// <summary>Pause before every request: 2 s is 30 a minute, under the register's 50.</summary>
    [Range(0, 60_000)]
    public int DelayMilliseconds { get; set; } = 2000;

    /// <summary>Nightly, after the Slovak run, before the morning imports.</summary>
    [Required]
    public string CronExpression { get; set; } = "45 3 * * *";

    /// <summary>
    /// Listings looked up per scheduled run, newest first. A listing costs one to three
    /// requests to justice.cz and one to ARES per company found; 800 stays under the
    /// register's daily 3 000.
    /// </summary>
    [Range(0, 10_000)]
    public int MaxListingsPerScheduledRun { get; set; } = 800;

    /// <summary>Listings an import may look up on the spot; a package goes to the nightly run.</summary>
    [Range(0, 1_000)]
    public int MaxListingsInline { get; set; } = 30;

    /// <summary>Rows asked for per search; the register offers 50, 200 or 500.</summary>
    [Range(50, 500)]
    public int PageSize { get; set; } = 500;

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
