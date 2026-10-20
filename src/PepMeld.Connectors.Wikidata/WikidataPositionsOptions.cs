using System.ComponentModel.DataAnnotations;
using PepMeld.Core.Domain;

namespace PepMeld.Connectors.Wikidata;

/// <summary>A Wikidata position (P39 value) read as a prominent public function.</summary>
public sealed class WikidataPosition
{
    /// <summary>The item, "Q18941264".</summary>
    [Required]
    public string Item { get; set; } = string.Empty;

    public Country Country { get; set; }
    public PepCategory Category { get; set; }

    [Required]
    public string Institution { get; set; } = string.Empty;

    [Required]
    public string Role { get; set; } = string.Empty;
}

/// <summary>Bound from "PepMeld:Connectors:Wikidata".</summary>
public sealed class WikidataPositionsOptions
{
    public const string SectionName = "PepMeld:Connectors:Wikidata";

    [Required]
    public string SparqlEndpoint { get; set; } = "https://query.wikidata.org/sparql";

    /// <summary>
    /// The positions read. The default is the Czech Senate alone: its own site publishes
    /// no date of birth and forbids robots, so Wikidata is the only source of the date,
    /// and the 348 senators since 1996 all carry one. More positions (judges, central
    /// bankers) are a configuration change, not code, once their coverage is checked.
    /// </summary>
    public List<WikidataPosition> Positions { get; set; } =
    [
        new()
        {
            Item = "Q18941264",
            Country = Country.CZ,
            Category = PepCategory.MemberOfParliament,
            Institution = "Senát Parlamentu ČR",
            Role = "senátor",
        },
    ];

    /// <summary>The query service asks for a pause between queries from one client.</summary>
    [Range(0, 60000)]
    public int DelayMilliseconds { get; set; } = 1000;
}
