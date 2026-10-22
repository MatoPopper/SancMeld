using System.ComponentModel.DataAnnotations;

namespace PepMeld.Connectors.CzRpp;

/// <summary>Bound from "PepMeld:Connectors:CzRpp".</summary>
public sealed class CzRppOptions
{
    public const string SectionName = "PepMeld:Connectors:CzRpp";

    /// <summary>
    /// The register of public authorities (rejstřík OVM) of the Registr práv a povinností,
    /// published by the Digital and Information Agency as open data and refreshed daily
    /// (about 45 MB, 19 564 authorities on 2026-10-22).
    /// </summary>
    [Required]
    public string Url { get; set; } = "https://rpp-opendata.egon.gov.cz/odrpp/datovasada/ovm.json";
}
