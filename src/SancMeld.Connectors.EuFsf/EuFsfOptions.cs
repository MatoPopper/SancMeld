using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.EuFsf;

/// <summary>
/// Configuration for the EU Consolidated Financial Sanctions List (FSF), published
/// by the European Commission on webgate.ec.europa.eu.
/// </summary>
public sealed class EuFsfOptions
{
    public const string SectionName = "SancMeld:Connectors:EuFsf";

    /// <summary>
    /// The download endpoint refuses to serve anything without a token: no token and
    /// an empty token both give 403, an invalid one gives 500 (verified 2026-08-06).
    ///
    /// This default is the token the Commission itself publishes on data.europa.eu —
    /// base64 of "token-2017" — not a personal registration. That matters: a personal
    /// token can be revoked without anyone noticing, whereas revoking the published
    /// one would break every consumer of the dataset, so it is the safer dependency.
    ///
    /// It stays configurable so a deployment can switch tokens without a redeploy.
    /// </summary>
    [Required]
    public string Token { get; set; } = "dG9rZW4tMjAxNw";

    /// <summary>
    /// The RSS feed is the change-detection mechanism, not a nicety. It is 3.9 kB and
    /// advertises, per format, the publication date, the byte length and the SHA-1 of
    /// the file — so a run that finds nothing new costs 3.9 kB instead of 25 MB.
    /// </summary>
    [Required]
    public Uri RssUrl { get; set; } = new("https://webgate.ec.europa.eu/fsd/fsf/public/rss");

    /// <summary>
    /// Which published artefact to consume. XML rather than CSV: the CSV is one row per
    /// cartesian combination of a subject's attributes (6 234 subjects become 43 851
    /// rows) and it drops the globalFileId that identifies the release.
    ///
    /// Matched against the URL path, never the RSS item title — the titles are prose
    /// ("XML (Based on XSD) - v1.1") and will be reworded eventually.
    /// </summary>
    [Required]
    public string FileVariant { get; set; } = "xmlFullSanctionsList_1_1";

    /// <summary>
    /// Refuse an import below this many subjects. The list held 6 234 on 2026-08-06 and
    /// grows; a real EU list will never legitimately fall to a few hundred, so a number
    /// this low can only mean a truncated download or a served error page.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumRecordCount { get; set; } = 4000;

    /// <summary>
    /// Check the downloaded bytes against the SHA-1 the RSS advertises. Leave this on:
    /// it is the one guard that catches a truncated transfer before the content is
    /// interpreted, which is exactly the failure that could otherwise empty a live list.
    /// </summary>
    public bool VerifyPublishedChecksum { get; set; } = true;

    internal Uri BuildDownloadUrl() =>
        new($"https://webgate.ec.europa.eu/fsd/fsf/public/files/{FileVariant}/content?token={Token}");
}
