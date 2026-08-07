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
    public static string Normalize(string? value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return string.Empty;
        }

        Span<char> buffer = value.Length <= 128 ? stackalloc char[value.Length] : new char[value.Length];
        var length = 0;

        foreach (var c in value)
        {
            if (char.IsLetterOrDigit(c))
            {
                buffer[length++] = char.ToUpperInvariant(c);
            }
        }

        return new string(buffer[..length]);
    }
}
