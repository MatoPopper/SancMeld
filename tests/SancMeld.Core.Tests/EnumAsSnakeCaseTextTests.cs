using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using Xunit;

namespace SancMeld.Core.Tests;

/// <summary>
/// Enum members are stored in the database as text, so their names are schema, not
/// code. The designation_current view filters on status = 'active'; rename
/// DesignationStatus.Active and that view silently stops returning rows instead of
/// failing to compile.
///
/// The literals below are therefore deliberately spelled out rather than derived —
/// a test that computes the expected value from the same code it is testing would
/// happily follow a rename.
/// </summary>
public class EnumAsSnakeCaseTextTests
{
    [Theory]
    [InlineData(DesignationStatus.Active, "active")]                    // designation_current depends on this
    [InlineData(DesignationStatus.PossiblyMissing, "possibly_missing")]
    [InlineData(DesignationStatus.Delisted, "delisted")]
    [InlineData(SubjectType.Person, "person")]
    [InlineData(SubjectType.Organisation, "organisation")]
    [InlineData(LegalEffect.Binding, "binding")]
    [InlineData(LegalEffect.Advisory, "advisory")]
    [InlineData(MeasureType.AssetFreeze, "asset_freeze")]
    [InlineData(MeasureType.EntryBan, "entry_ban")]
    [InlineData(NameType.NativeScript, "native_script")]
    [InlineData(IdentifierType.NationalId, "national_id")]
    [InlineData(IdentifierType.CompanyRegistration, "company_registration")]
    [InlineData(IdentifierType.Imo, "imo")]
    [InlineData(AdjudicationDecision.NeedsMoreData, "needs_more_data")]
    [InlineData(DatePrecision.Circa, "circa")]
    public void StoredForm_IsStable<T>(T value, string expected)
        where T : struct, Enum
        => Assert.Equal(expected, Convert(value));

    [Theory]
    [MemberData(nameof(EveryMember))]
    public void RoundTrip_PreservesTheValue(object value)
    {
        var type = value.GetType();
        var converter = Create(type);
        var stored = converter.ConvertToProvider(value);

        Assert.Equal(value, converter.ConvertFromProvider(stored));
    }

    // Every member of every enum the model stores, so adding one cannot slip through
    // untested — a member whose name breaks the round trip fails here immediately.
    public static TheoryData<object> EveryMember()
    {
        var data = new TheoryData<object>();
        foreach (var type in new[]
        {
            typeof(SubjectType), typeof(LegalEffect), typeof(DatePrecision),
            typeof(DesignationStatus), typeof(MeasureType), typeof(NameType),
            typeof(IdentifierType), typeof(AdjudicationDecision),
        })
        {
            foreach (var member in Enum.GetValues(type))
            {
                data.Add(member);
            }
        }

        return data;
    }

    private static string Convert<T>(T value) where T : struct, Enum
        => (string)new EnumAsSnakeCaseText<T>().ConvertToProvider(value)!;

    private static Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter Create(Type enumType)
        => (Microsoft.EntityFrameworkCore.Storage.ValueConversion.ValueConverter)
           Activator.CreateInstance(typeof(EnumAsSnakeCaseText<>).MakeGenericType(enumType))!;
}
