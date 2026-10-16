using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.Ares;

/// <summary>
/// ARES, the Czech register of economic subjects, read for who owns and runs a Czech
/// company: its public-register part lists shareholders and statutory bodies, each person
/// with a date of birth and each share with its size and dates. Free, no key, run by the
/// Ministry of Finance.
///
/// Read on demand — a company is looked up when someone asks about it or a case file
/// names it — not crawled: there are three million subjects and a sanctions question
/// concerns a handful. Only the people who turn out to be listed are kept; everyone else
/// ARES names is read, compared and forgotten.
/// </summary>
public sealed class AresOptions
{
    public const string SectionName = "SancMeld:Ares";

    /// <summary>Off means no call to ARES; stored links keep serving.</summary>
    public bool Enabled { get; set; } = true;

    [Required]
    public string BaseUrl { get; set; } = "https://ares.gov.cz/ekonomicke-subjekty-v-be/rest";

    /// <summary>
    /// How many shareholder hops are followed from the company asked about: a company
    /// owned by a Czech holding owned by a person. Each hop is one more call per holder.
    /// </summary>
    [Range(0, 5)]
    public int MaxDepth { get; set; } = 2;

    /// <summary>Companies a name search may resolve to before it gives up and asks for an IČO.</summary>
    [Range(1, 20)]
    public int MaxNameMatches { get; set; } = 3;

    /// <summary>
    /// How alike a company name returned by the search must be to the one asked about,
    /// as trigram similarity: "Torgretail" finds "Torgretail CZ s.r.o.", not every
    /// company with "retail" in its name.
    /// </summary>
    [Range(0.2, 1.0)]
    public double MinCompanyNameScore { get; set; } = 0.5;

    [Range(0.3, 1.0)]
    public double MinPersonNameScore { get; set; } = 0.6;

    [Range(5, 300)]
    public int TimeoutSeconds { get; set; } = 30;
}
