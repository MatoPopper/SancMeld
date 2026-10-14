using System.Text;

namespace SancMeld.Experiments.NameEmbeddings;

/// <summary>
/// Latin or not, by the letters a name contains.
///
/// The database has a script_code column and nothing fills it yet, so the experiment
/// decides for itself. "Latin" is generous on purpose — accents, Vietnamese tone marks and
/// Turkish dotless i all count — because the question is whether a reader who writes in
/// the Latin alphabet could have typed the recorded spelling, not which Unicode block it
/// sits in. Punctuation and digits decide nothing; a name made only of those is Latin.
/// </summary>
public static class Script
{
    public static bool IsLatin(string name)
    {
        foreach (var rune in name.EnumerateRunes())
        {
            if (!Rune.IsLetter(rune))
            {
                continue;
            }

            // Basic Latin through Latin Extended-B, then Latin Extended Additional.
            var latin = rune.Value <= 0x024F || rune.Value is >= 0x1E00 and <= 0x1EFF;

            if (!latin)
            {
                return false;
            }
        }

        return true;
    }
}
