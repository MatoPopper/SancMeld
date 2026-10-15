using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.UkFcdo;

/// <summary>
/// Configuration for the UK Sanctions List, published by the Foreign, Commonwealth &amp;
/// Development Office under the Sanctions and Anti-Money Laundering Act 2018.
/// </summary>
public sealed class UkFcdoOptions
{
    public const string SectionName = "SancMeld:Connectors:UkFcdo";

    /// <summary>
    /// The XML at its stable address. The same content is offered as CSV, ODS, HTML,
    /// TXT and PDF; the XML is the one with a published schema (SanctionsListSchema
    /// 4.33.3) and the only one that keeps names in their parts. The server answers
    /// conditional requests with an ETag and a Last-Modified (verified 2026-10-15).
    /// </summary>
    [Required]
    public Uri DownloadUrl { get; set; } = new("https://sanctionslist.fcdo.gov.uk/docs/UK-Sanctions-List.xml");

    /// <summary>
    /// Refuse an import below this many designations. The list held 6 408 on 2026-10-15
    /// and grows; a real UK list will never fall to a few hundred, so a number this low
    /// means a truncated download or a page served as the file.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumRecordCount { get; set; } = 4000;
}
