namespace SancMeld.Core.Matching;

/// <summary>
/// The comparison form of a document number.
///
/// Identifiers are matched exactly, so the form has to survive the ways publishers and
/// analysts write the same number: "AB-123 456", "ab123456" and "AB123456" are one
/// passport. Deliberately not <see cref="NameNormalizer"/>, which lowercases and keeps
/// word boundaries — right for names, wrong here.
///
/// Every side of a comparison must use this, or a stored identifier and a queried one
/// will never meet.
/// </summary>
public static class IdentifierNormalizer
{
    /// <summary>
    /// Words that introduce a number rather than being part of one. Short enough that the
    /// length rule below would keep them, and common enough to be worth naming.
    /// </summary>
    private static readonly string[] NumberMarkers = ["no", "nr", "id", "n"];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        var core = Core(value);

        Span<char> buffer = core.Length <= 128 ? stackalloc char[core.Length] : new char[core.Length];
        var length = 0;

        foreach (var c in core)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }

    /// <summary>
    /// The number inside the sentence the publisher wrote around it.
    ///
    /// The UN puts prose in the field that is supposed to hold a document number —
    /// "Pakistani passport number AA3829182", "R00012098, South Sudan", "Diplomatic
    /// Passport: Z30321794" — and the EU does it too, more rarely ("Ration Card no.
    /// 0094195", "A 0139243 German travel document"). Without this, the stored form of the
    /// first is PAKISTANIPASSPORTNUMBERAA3829182, which no officer will ever type, so the
    /// one comparison in this system that is not a similarity judgement silently never
    /// fires. Measured on the current lists: 68 of 509 UN identifiers and 23 of 3 008 EU
    /// ones are written this way.
    ///
    /// Only the ends are trimmed, and only words carrying no digit at all. Anything with a
    /// digit stops the trim, so a prefix that is part of the number survives — "SE 012820"
    /// keeps its SE, "A 0139243" its A, "HRB 81573" loses its HRB only because both the
    /// stored side and the typed side lose it, which is what keeps them equal. Trimming
    /// the middle as well would turn "8208 No. 555627" into a number the publisher never
    /// printed.
    /// </summary>
    private static string Core(string value)
    {
        // Remarks the publisher put in brackets: "(Expired 25 Jan. 2016)", "(US)". Their
        // digits are dates and would otherwise look like part of the number.
        var withoutRemarks = RemoveBracketed(value);

        var words = withoutRemarks.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);

        var first = 0;
        var last = words.Length - 1;

        while (first <= last && IsProse(words[first])) first++;
        while (last >= first && IsProse(words[last])) last--;

        // Everything was prose: no number was found, so nothing is a better guess than
        // the value as published. A BIC like "DCBK KPPY" arrives here.
        return first > last ? withoutRemarks : string.Join(' ', words[first..(last + 1)]);
    }

    private static bool IsProse(string word)
    {
        var trimmed = word.Trim(TrimmableAround);

        if (trimmed.Length == 0 || trimmed.Any(char.IsDigit))
        {
            return false;
        }

        // Three letters is where a word stops looking like the letter part of a document
        // number. Below it the only safe reading is the explicit markers: dropping "SE"
        // from "passport number SE 012820" would leave a number that belongs to nobody.
        return trimmed.Length >= 3
            || NumberMarkers.Contains(trimmed, StringComparer.OrdinalIgnoreCase);
    }

    private static readonly char[] TrimmableAround = ['.', ',', ':', ';', '(', ')', '"', '\'', '-', '/', '#', '°'];

    private static string RemoveBracketed(string value)
    {
        if (!value.Contains('(', StringComparison.Ordinal))
        {
            return value;
        }

        var kept = new System.Text.StringBuilder(value.Length);
        var depth = 0;

        foreach (var c in value)
        {
            if (c == '(') depth++;
            else if (c == ')' && depth > 0) depth--;
            else if (depth == 0) kept.Append(c);
        }

        return kept.ToString();
    }
}
