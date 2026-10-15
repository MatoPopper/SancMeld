using System.ComponentModel.DataAnnotations;

namespace SancMeld.Connectors.CzMzv;

/// <summary>
/// Configuration for the Czech national sanctions list (vnitrostátní sankční seznam),
/// which the Ministry of Foreign Affairs keeps under Act No. 1/2023 Coll. (the
/// sanctions act).
/// </summary>
public sealed class CzMzvOptions
{
    public const string SectionName = "SancMeld:Connectors:CzMzv";

    /// <summary>
    /// The ministry's open-data page, where the list is linked as a CSV. The file's
    /// address carries an id and a date that change with every release
    /// (/file/6248997/Vnitrostatni_sankcni_seznam_2026_07_23.csv on 2026-10-15), so
    /// there is no download URL to configure; the connector finds the current one here.
    /// </summary>
    [Required]
    public Uri OpenDataPageUrl { get; set; } =
        new("https://mzv.gov.cz/jnp/cz/o_ministerstvu/otevrena_data/index_5.html");

    /// <summary>
    /// Refuse an import below this many rows. The list is small by nature — 26 entries
    /// on 2026-10-15, delisted and superseded ones included — so the guard is low, but
    /// a file with a handful of rows is still far likelier to be a served error page
    /// than a list that shrank to it.
    /// </summary>
    [Range(1, int.MaxValue)]
    public int MinimumRecordCount { get; set; } = 10;
}
