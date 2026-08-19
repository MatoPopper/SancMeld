using SancMeld.Core.Matching;
using Xunit;

namespace SancMeld.Core.Tests;

/// <summary>
/// Every case here is a value taken verbatim from the EU or UN list as published. An
/// identifier match is the one answer in this system that is not a similarity judgement,
/// so it is also the one that fails most quietly: if the stored form and the typed form
/// differ by a single character, the check simply never fires and the subject comes back
/// as an ordinary name hit.
/// </summary>
public class IdentifierNormalizerTests
{
    [Theory]
    // Already a number. These must come through untouched, prose rules or not.
    [InlineData("AA3829182", "AA3829182")]
    [InlineData("ab-123 456", "AB123456")]
    [InlineData("12CP63882.3FRA", "12CP638823FRA")]
    [InlineData("BNSDLA70L14Z352B", "BNSDLA70L14Z352B")]
    [InlineData("C17HLRL3", "C17HLRL3")]
    // The UN writes a sentence into the number field.
    [InlineData("Pakistani passport number AA3829182", "AA3829182")]
    [InlineData("Pakistani national identification number 5420124679187", "5420124679187")]
    [InlineData("Diplomatic Passport: Z30321794", "Z30321794")]
    [InlineData("CAR diplomatic passport no. D00000898", "D00000898")]
    [InlineData("Libyan Passport No. 96/184442", "96184442")]
    [InlineData("Provisional passport No.: 28642163", "28642163")]
    [InlineData("Afghan national identification card (tazkira) number 300786", "300786")]
    [InlineData("Cedula No. 52252815", "52252815")]
    [InlineData("Jordan 286062", "286062")]
    [InlineData("Libya B/002210", "B002210")]
    // Trailing prose, and remarks in brackets whose digits are dates.
    [InlineData("R00012098, South Sudan", "R00012098")]
    [InlineData("381110042 (Expired 25 Jan. 2016)", "381110042")]
    [InlineData("Social Security Number 423-31-3021 (US)", "423313021")]
    [InlineData("A 0139243 German travel document", "A0139243")]
    // The EU does it too, less often.
    [InlineData("Ration Card no. 0094195", "0094195")]
    [InlineData("BPA Nr. 1336597587", "1336597587")]
    [InlineData("Group ID: 16463", "16463")]
    public void Normalize_FindsTheNumberInsideWhatWasPublished(string published, string expected)
        => Assert.Equal(expected, IdentifierNormalizer.Normalize(published));

    /// <summary>
    /// A short letter group next to the digits is part of the number, not a description of
    /// it. Getting this wrong is worse than not trimming at all: it produces a shorter
    /// number that belongs to somebody else.
    /// </summary>
    [Theory]
    [InlineData("Afghan passport number SE 012820", "SE012820")]
    [InlineData("German travel document A 0139243", "A0139243")]
    [InlineData("Russian passport number 8208 No. 555627", "8208NO555627")]
    public void Normalize_KeepsLetterGroupsThatBelongToTheNumber(string published, string expected)
        => Assert.Equal(expected, IdentifierNormalizer.Normalize(published));

    /// <summary>
    /// No digits anywhere means no number was found, and the value as published is a
    /// better answer than nothing. BIC codes and place names arrive here.
    /// </summary>
    [Theory]
    [InlineData("DCBK KPPY", "DCBKKPPY")]
    [InlineData("Nova Kakhovka", "NOVAKAKHOVKA")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void Normalize_FallsBackToTheWholeValueWhenThereIsNoNumber(string? published, string expected)
        => Assert.Equal(expected, IdentifierNormalizer.Normalize(published));

    /// <summary>
    /// The property the whole thing rests on: whatever an officer types and whatever the
    /// publisher printed have to land on the same string. Both sides call this method, so
    /// the test is that the method agrees with itself on the two spellings of one document.
    /// </summary>
    [Theory]
    [InlineData("Pakistani passport number AA3829182", "AA3829182")]
    [InlineData("R00012098, South Sudan", "R00012098")]
    [InlineData("HRB 81573", "HRB 81573")]
    [InlineData("Ration Card Number 0075258", "0075258")]
    public void Normalize_AgreesBetweenThePublishedFormAndTheTypedForm(string published, string typed)
        => Assert.Equal(IdentifierNormalizer.Normalize(published), IdentifierNormalizer.Normalize(typed));
}
