using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.NlBz;

/// <summary>
/// Configuration for the Dutch national terrorism sanctions list (Nationale
/// sanctielijst terrorisme), kept by the Minister of Foreign Affairs under the
/// Sanctieregeling terrorisme 2007-II.
/// </summary>
public sealed class NlBzOptions
{
    public const string SectionName = "SancMeld:Connectors:NlBz";

    /// <summary>
    /// The spreadsheet itself, at an address that has not moved since the list was
    /// first published there in 2015. The server answers conditional requests with an
    /// ETag and a Last-Modified (verified 2026-10-15), so an unchanged list costs one
    /// 304 and no payload.
    /// </summary>
    [Required]
    public Uri DownloadUrl { get; set; } =
        new("https://www.rijksoverheid.nl/site/binaries/site-content/collections/documents/2015/08/27/nationale-terrorismelijst/ned-terrorismelijst.ods");

    /// <summary>
    /// Refuse an import below this many entries. The list held 117 on 2026-10-15 — 114
    /// persons and 3 organisations — and changes by ones and twos; a spreadsheet with a
    /// few dozen rows is a different document.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumRecordCount { get; set; } = 50;
}
