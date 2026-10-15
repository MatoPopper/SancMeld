using System.Text.RegularExpressions;
using SancMeld.Core.Domain;

namespace SancMeld.Connectors.PlMswia;

/// <summary>
/// The measures column, a lettered list in prose with footnote markers:
///
///   a. zamrożenie wszystkich środków finansowych i zasobów gospodarczych[1],
///   b. zakaz udostępniania osobie wpisanej na listę … środków finansowych …[2],
///   c. zakaz świadomego i umyślnego udziału w działaniach … ominięcie środków …,
///   d. wpis do wykazu cudzoziemców, których pobyt … jest niepożądany[3]
///
/// Read by keyword, not by letter: the letters restart per row and the wording varies
/// ("funduszy" / "środków finansowych" / "środków finasowych"). Item c is an obligation
/// on third parties not to help circumvent a and b, not a measure against the subject,
/// and gets no entry. Exclusion from public procurement has no type of its own in the
/// model and is recorded as Other; the verbatim column travels in Remarks.
/// </summary>
internal static partial class PlMeasures
{
    public static (List<MeasureType> Measures, List<string> Programmes) Parse(string cell)
    {
        var measures = new List<MeasureType>();
        var programmes = new List<string>();

        foreach (var line in cell.Split('\n'))
        {
            var text = line.Trim();

            if (text.Length == 0)
            {
                continue;
            }

            var measure = text switch
            {
                _ when Contains(text, "zamrożenie") => MeasureType.AssetFreeze,
                _ when Contains(text, "zakaz udostępniania") => MeasureType.FundsProhibition,
                _ when Contains(text, "wykaz cudzoziemc") || Contains(text, "wykazu cudzoziemc") => MeasureType.EntryBan,
                _ when Contains(text, "wykluczenie z postępowania") => MeasureType.Other,
                _ => (MeasureType?)null,
            };

            if (measure is { } kind && !measures.Contains(kind))
            {
                measures.Add(kind);
            }
        }

        // The footnotes say which EU regulation defines the frozen assets: [1] and [2]
        // Regulation 269/2014 (Russia / Ukraine), [4] and [5] Regulation 765/2006
        // (Belarus). Those are the programme codes the EU list itself uses for the same
        // regulations, so a Polish listing lands beside its EU counterpart.
        foreach (Match marker in FootnotePattern().Matches(cell))
        {
            var programme = marker.Groups[1].Value switch
            {
                "1" or "2" => "UKR",
                "4" or "5" => "BLR",
                _ => null,
            };

            if (programme is not null && !programmes.Contains(programme))
            {
                programmes.Add(programme);
            }
        }

        return (measures, programmes);
    }

    private static bool Contains(string text, string needle) =>
        text.Contains(needle, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"\[(\d+)\]")]
    private static partial Regex FootnotePattern();
}
