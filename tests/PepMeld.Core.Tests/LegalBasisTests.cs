using PepMeld.Core.Domain;
using Xunit;

namespace PepMeld.Core.Tests;

public class LegalBasisTests
{
    [Theory]
    [InlineData(PepCategory.HeadOfState, "a")]
    [InlineData(PepCategory.GovernmentMember, "a")]
    [InlineData(PepCategory.MemberOfParliament, "b")]
    [InlineData(PepCategory.SeniorJudge, "c")]
    [InlineData(PepCategory.CentralBankBoard, "d")]
    [InlineData(PepCategory.Ambassador, "e")]
    [InlineData(PepCategory.SeniorOfficer, "f")]
    [InlineData(PepCategory.StateEnterpriseBoard, "g")]
    [InlineData(PepCategory.Prosecutor, "h")]
    [InlineData(PepCategory.EuOrInternational, "i")]
    [InlineData(PepCategory.OtherProminentFunction, "i")]
    [InlineData(PepCategory.PartyLeadership, "j")]
    public void SlovakLettersFollowParagraphSix(PepCategory category, string letter)
        => Assert.Equal($"§ 6 ods. 2 písm. {letter}) zákona č. 297/2008 Z. z.", LegalBasis.National(Country.SK, category));

    [Fact]
    public void CzechBasisIsOneParagraphForEveryCategory()
    {
        foreach (var category in Enum.GetValues<PepCategory>())
        {
            Assert.Equal("§ 4 odst. 5 písm. a) zákona č. 253/2008 Sb.", LegalBasis.National(Country.CZ, category));
        }
    }

    [Fact]
    public void EveryCategoryHasAnAmlrMapping()
    {
        foreach (var category in Enum.GetValues<PepCategory>())
        {
            Assert.Contains("2024/1624", LegalBasis.Amlr(category));
        }
    }
}
