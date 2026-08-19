using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.FrGels;

/// <summary>
/// Configuration for the French national asset-freeze register, published by the
/// Direction générale du Trésor.
/// </summary>
public sealed class FrGelsOptions
{
    public const string SectionName = "SancMeld:Connectors:FrGels";

    /// <summary>
    /// The publisher's own API, which always serves the current publication. There is no
    /// catalogue and no version marker to read beforehand; the publication carries its own
    /// date inside the payload.
    /// </summary>
    [Required]
    public Uri DownloadUrl { get; set; } =
        new("https://gels-avoirs.dgtresor.gouv.fr/ApiPublic/api/v1/publication/derniere-publication-fichier-json");

    /// <summary>
    /// The register held 6 578 records on 2026-08-19 and has never been near this low.
    /// A smaller answer means a truncated download or an error page served as 200, and
    /// must not be allowed to publish.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumRecordCount { get; set; } = 4000;
}
