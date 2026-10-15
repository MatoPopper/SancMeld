using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// Configuration for the Polish sanctions list, which the Minister of the Interior and
/// Administration (MSWiA) keeps under the act of 13 April 2022 on counteracting support
/// for the aggression against Ukraine.
/// </summary>
public sealed class PlMswiaOptions
{
    public const string SectionName = "SancMeld:Connectors:PlMswia";

    /// <summary>
    /// The page that carries the list. There is no download URL to configure: the
    /// workbook is a gov.pl attachment addressed by a GUID that changes with every
    /// release (verified 2026-10-15: "tabela_lista_sankcyjna_26_05_2026.xlsx" sits at
    /// /attachment/efe8ad28-…), so the connector finds the current one on this page.
    /// </summary>
    [Required]
    public Uri LandingPageUrl { get; set; } =
        new("https://www.gov.pl/web/mswia/lista-osob-i-podmiotow-objetych-sankcjami");

    /// <summary>
    /// Refuse an import below this many rows. The workbook held 445 persons and 139
    /// entities on 2026-10-15, delisted ones included, and has only grown since the act
    /// came into force. A number this low means a truncated download or a page served in
    /// place of the file.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumRecordCount { get; set; } = 400;
}
