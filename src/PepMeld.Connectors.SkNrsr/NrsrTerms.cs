namespace PepMeld.Connectors.SkNrsr;

/// <summary>
/// The terms of the National Council as the site numbers them, with the dates of the
/// constituent sessions that begin and end them. The site gives no mandate dates per
/// person, so these bounds are what a membership carries, and the finding says so: a
/// substitute who came in halfway through the term is reported with the term's dates.
///
/// A term the site offers and this table does not know is read with no dates; add the
/// row when the next parliament convenes.
/// </summary>
internal static class NrsrTerms
{
    public static readonly IReadOnlyDictionary<int, (DateOnly From, DateOnly? To)> Bounds = new Dictionary<int, (DateOnly, DateOnly?)>
    {
        [3] = (new DateOnly(2002, 10, 15), new DateOnly(2006, 7, 4)),
        [4] = (new DateOnly(2006, 7, 4), new DateOnly(2010, 7, 8)),
        [5] = (new DateOnly(2010, 7, 8), new DateOnly(2012, 4, 4)),
        [6] = (new DateOnly(2012, 4, 4), new DateOnly(2016, 3, 23)),
        [7] = (new DateOnly(2016, 3, 23), new DateOnly(2020, 3, 20)),
        [8] = (new DateOnly(2020, 3, 20), new DateOnly(2023, 10, 25)),
        [9] = (new DateOnly(2023, 10, 25), null),
    };

    public const string DateNote = "Dátumy sú hranice volebného obdobia; presný začiatok a koniec mandátu (náhradníci, vzdanie sa mandátu) zdroj neuvádza.";

    /// <summary>"3-9", "9" or "7,8,9" into the terms, ascending.</summary>
    public static IReadOnlyList<int> Parse(string terms)
    {
        var result = new SortedSet<int>();

        foreach (var part in terms.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 2 && int.TryParse(range[0], out var from) && int.TryParse(range[1], out var to) && from <= to)
            {
                for (var t = from; t <= to; t++)
                {
                    result.Add(t);
                }
            }
            else if (int.TryParse(part, out var single))
            {
                result.Add(single);
            }
            else
            {
                throw new FormatException($"'{terms}' is not a list of terms; write it as 3-9, 9 or 7,8,9.");
            }
        }

        return [.. result];
    }
}
