using System.ComponentModel.DataAnnotations;

namespace PepMeld.Connectors.CzAres;

/// <summary>Bound from "PepMeld:Connectors:CzAres".</summary>
public sealed class CzAresOptions
{
    public const string SectionName = "PepMeld:Connectors:CzAres";

    [Required]
    public string BaseUrl { get; set; } = "https://ares.gov.cz/ekonomicke-subjekty-v-be/rest";

    /// <summary>
    /// Legal forms whose every subject is read: 301 státní podnik, 302 národní podnik. A
    /// state enterprise is state-owned by definition, so the register's own search finds
    /// them all (92 on 2026-10-20). Companies the state owns as a shareholder cannot be
    /// searched for and come from StateEntities.json.
    /// </summary>
    [Required]
    public string LegalForms { get; set; } = "301,302";

    /// <summary>Pause between two ARES requests. The service is fast and public; this is courtesy.</summary>
    [Range(0, 10000)]
    public int DelayMilliseconds { get; set; } = 200;

    /// <summary>
    /// Seats that ended more than this many years ago are not imported. The register
    /// keeps every member since 1992; a seat that ended twenty years ago makes nobody a
    /// politically exposed person today, and 240 historic supervisory-board rows of one
    /// utility would bury the current ones.
    /// </summary>
    [Range(1, 50)]
    public int HistoryYears { get; set; } = 10;
}
