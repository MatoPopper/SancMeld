using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.UnSc;

/// <summary>
/// Configuration for the UN Security Council Consolidated List.
/// </summary>
public sealed class UnScOptions
{
    public const string SectionName = "SancMeld:Connectors:UnSc";

    /// <summary>
    /// The stable address. It answers 302 with a one-hour signed Azure Blob URL, so the
    /// resolved location must never be stored or reused — only this one is durable.
    /// </summary>
    [Required]
    public Uri DownloadUrl { get; set; } = new("https://scsanctions.un.org/resources/xml/en/consolidated.xml");

    /// <summary>
    /// The list held 1 011 subjects on 2026-08-08 and changes slowly. A number this low can
    /// only mean a truncated download or an error page served as 200.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumRecordCount { get; set; } = 700;

    /// <summary>
    /// Check the downloaded bytes against the Content-MD5 the blob advertises. MD5 is not
    /// a security choice here — it is the header Azure sends, and it catches a truncated
    /// transfer before a single record is interpreted.
    /// </summary>
    public bool VerifyContentMd5 { get; set; } = true;
}
