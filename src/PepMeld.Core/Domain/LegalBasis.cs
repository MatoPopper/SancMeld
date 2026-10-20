namespace PepMeld.Core.Domain;

/// <summary>
/// The paragraph behind a category, in the words a compliance officer cites. Kept as text
/// on every finding, not looked up when it is shown: the law may be amended, and a
/// finding made under the old wording must keep saying so.
/// </summary>
public static class LegalBasis
{
    public static string National(Country country, PepCategory category) => country switch
    {
        Country.SK => $"§ 6 ods. 2 písm. {SkLetter(category)}) zákona č. 297/2008 Z. z.",

        // One paragraph lists every function; there is no letter to cite per category.
        Country.CZ => "§ 4 odst. 5 písm. a) zákona č. 253/2008 Sb.",
        _ => throw new ArgumentOutOfRangeException(nameof(country), country, null),
    };

    /// <summary>
    /// Regulation (EU) 2024/1624, article 2(1)(34), which applies from 10 July 2027. Recorded
    /// now so that the findings made before that date can be read under the new rules
    /// without being made again. Verify against AMLA's guidance once it is published.
    /// </summary>
    public static string Amlr(PepCategory category) => category switch
    {
        PepCategory.EuOrInternational => "čl. 2 ods. 1 bod 34 nariadenia (EÚ) 2024/1624 (funkcia na úrovni EÚ alebo medzinárodnej organizácie)",
        _ => $"čl. 2 ods. 1 bod 34 písm. {AmlrLetter(category)}) nariadenia (EÚ) 2024/1624",
    };

    private static string SkLetter(PepCategory category) => category switch
    {
        PepCategory.HeadOfState or PepCategory.GovernmentMember => "a",
        PepCategory.MemberOfParliament => "b",
        PepCategory.SeniorJudge => "c",
        PepCategory.CourtOfAuditors or PepCategory.CentralBankBoard => "d",
        PepCategory.Ambassador => "e",
        PepCategory.SeniorOfficer => "f",
        PepCategory.StateEnterpriseBoard => "g",
        PepCategory.Prosecutor => "h",
        PepCategory.RegionalLeader or PepCategory.EuOrInternational => "i",
        PepCategory.PartyLeadership => "j",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };

    private static string AmlrLetter(PepCategory category) => category switch
    {
        PepCategory.HeadOfState or PepCategory.GovernmentMember => "a",
        PepCategory.MemberOfParliament => "b",
        PepCategory.PartyLeadership => "c",
        PepCategory.SeniorJudge => "d",
        PepCategory.CourtOfAuditors or PepCategory.CentralBankBoard => "e",
        PepCategory.Ambassador or PepCategory.SeniorOfficer => "f",
        PepCategory.StateEnterpriseBoard => "g",
        PepCategory.RegionalLeader => "h",

        // Prosecutors are a Slovak addition under "other prominent public functions
        // provided for by a Member State", which the regulation keeps as letter i).
        PepCategory.Prosecutor => "i",
        _ => throw new ArgumentOutOfRangeException(nameof(category), category, null),
    };
}
