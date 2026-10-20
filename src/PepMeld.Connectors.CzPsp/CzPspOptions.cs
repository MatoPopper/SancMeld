using System.ComponentModel.DataAnnotations;

namespace PepMeld.Connectors.CzPsp;

/// <summary>Bound from "PepMeld:Connectors:CzPsp".</summary>
public sealed class CzPspOptions
{
    public const string SectionName = "PepMeld:Connectors:CzPsp";

    /// <summary>
    /// The persons-and-bodies dump of the Chamber of Deputies' open data, refreshed by the
    /// publisher daily. Documented at https://www.psp.cz/sqw/hp.sqw?k=1301.
    /// </summary>
    [Required]
    public string Url { get; set; } = "https://www.psp.cz/eknih/cdrom/opendata/poslanci.zip";
}
