using System.Text.RegularExpressions;
using PepMeld.Core.Domain;

namespace PepMeld.Connectors.SkNrsrVnf;

/// <summary>One function as a declaration states it: the office, and the organisation in parentheses when there is one.</summary>
internal sealed record DeclaredFunction(string Role, string? Organisation, PepCategory Category, string Institution);

/// <summary>
/// Maps the wording of a declaration to a category. The register follows čl. 2 ods. 1
/// of ústavný zákon č. 357/2004 Z. z., whose letters a) to zp) are finer than the ten of
/// § 6 ods. 2 zák. č. 297/2008 Z. z.; the wording is matched by phrase, in order, and
/// anything unmatched is a prominent function of national significance under i), which
/// is what the Slovak law itself says about offices it does not name.
/// </summary>
internal static partial class VnfFunctions
{
    [GeneratedRegex(@"^(?<role>[^()]*?)\s*\((?<org>.+)\)\s*$", RegexOptions.CultureInvariant | RegexOptions.Singleline)]
    private static partial Regex RoleAndOrganisation();

    private static readonly (string Phrase, PepCategory Category, string Institution)[] Rules =
    [
        // Public institutions and regulators first: their boards are "dozorná rada" and
        // "správna rada" too, but they are not state enterprises under letter g).
        ("sociálnej poisťovne", PepCategory.OtherProminentFunction, "Sociálna poisťovňa"),
        ("rozhlasu a televízie", PepCategory.OtherProminentFunction, "Rozhlas a televízia Slovenska"),
        ("rtvs", PepCategory.OtherProminentFunction, "Rozhlas a televízia Slovenska"),
        ("tlačovej agentúry", PepCategory.OtherProminentFunction, "Tlačová agentúra Slovenskej republiky"),
        ("ústavu pamäti národa", PepCategory.OtherProminentFunction, "Ústav pamäti národa"),
        ("pozemkového fondu", PepCategory.OtherProminentFunction, "Slovenský pozemkový fond"),
        ("úradu pre dohľad nad zdravotnou starostlivosťou", PepCategory.OtherProminentFunction, "Úrad pre dohľad nad zdravotnou starostlivosťou"),
        ("rady pre rozpočtovú zodpovednosť", PepCategory.OtherProminentFunction, "Rada pre rozpočtovú zodpovednosť"),
        ("regulačnej rady", PepCategory.OtherProminentFunction, "Úrad pre reguláciu sieťových odvetví"),
        ("úradu pre reguláciu", PepCategory.OtherProminentFunction, "regulačný úrad"),
        ("dopravného úradu", PepCategory.OtherProminentFunction, "Dopravný úrad"),
        ("rady pre vysielanie", PepCategory.OtherProminentFunction, "Rada pre mediálne služby"),
        ("rady pre mediálne služby", PepCategory.OtherProminentFunction, "Rada pre mediálne služby"),
        ("ochranu osobných údajov", PepCategory.OtherProminentFunction, "Úrad na ochranu osobných údajov"),
        ("finančného riaditeľstva", PepCategory.OtherProminentFunction, "Finančné riaditeľstvo Slovenskej republiky"),
        ("kancelárie", PepCategory.OtherProminentFunction, "kancelária ústavného orgánu"),
        ("ochranca práv", PepCategory.OtherProminentFunction, "verejný ochranca práv"),
        ("komisár", PepCategory.OtherProminentFunction, "komisár"),

        // The state-owned health insurer is a joint-stock company of the state: letter g).
        ("všeobecnej zdravotnej poisťovne", PepCategory.StateEnterpriseBoard, "Všeobecná zdravotná poisťovňa, a.s."),
        ("všzp", PepCategory.StateEnterpriseBoard, "Všeobecná zdravotná poisťovňa, a.s."),

        ("prezident slovenskej republiky", PepCategory.HeadOfState, "Slovenská republika"),
        ("národnej rady", PepCategory.MemberOfParliament, "Národná rada Slovenskej republiky"),
        ("člen vlády", PepCategory.GovernmentMember, "Vláda Slovenskej republiky"),
        ("predseda vlády", PepCategory.GovernmentMember, "Vláda Slovenskej republiky"),
        ("minister", PepCategory.GovernmentMember, "Vláda Slovenskej republiky"),
        ("štátny tajomník", PepCategory.GovernmentMember, "Vláda Slovenskej republiky"),
        ("ústredného orgánu štátnej správy", PepCategory.GovernmentMember, "ústredný orgán štátnej správy"),
        ("generálny tajomník služobného úradu", PepCategory.GovernmentMember, "ústredný orgán štátnej správy"),
        ("ústavného súdu", PepCategory.SeniorJudge, "Ústavný súd Slovenskej republiky"),
        ("najvyššieho súdu", PepCategory.SeniorJudge, "Najvyšší súd Slovenskej republiky"),
        ("najvyššieho správneho súdu", PepCategory.SeniorJudge, "Najvyšší správny súd Slovenskej republiky"),
        ("súdnej rady", PepCategory.SeniorJudge, "Súdna rada Slovenskej republiky"),
        ("sudca", PepCategory.SeniorJudge, "súd"),
        ("prokurátor", PepCategory.Prosecutor, "prokuratúra"),
        ("bankovej rady", PepCategory.CentralBankBoard, "Národná banka Slovenska"),
        ("najvyššieho kontrolného úradu", PepCategory.CourtOfAuditors, "Najvyšší kontrolný úrad Slovenskej republiky"),
        ("primátor", PepCategory.RegionalLeader, "mesto"),
        ("vyššieho územného celku", PepCategory.RegionalLeader, "vyšší územný celok"),
        ("predseda vúc", PepCategory.RegionalLeader, "vyšší územný celok"),
        ("generálneho štábu", PepCategory.SeniorOfficer, "Ozbrojené sily Slovenskej republiky"),
        ("policajného zboru", PepCategory.SeniorOfficer, "Policajný zbor"),
        ("vojenského spravodajstva", PepCategory.SeniorOfficer, "Vojenské spravodajstvo"),
        ("slovenskej informačnej služby", PepCategory.SeniorOfficer, "Slovenská informačná služba"),
        ("štátneho podniku", PepCategory.StateEnterpriseBoard, "štátny podnik"),
        ("štatutárneho orgánu", PepCategory.StateEnterpriseBoard, "právnická osoba ustanovená štátom"),
        ("štatutárny orgán", PepCategory.StateEnterpriseBoard, "právnická osoba ustanovená štátom"),
        ("riadiaceho orgánu", PepCategory.StateEnterpriseBoard, "právnická osoba ustanovená štátom"),
        ("kontrolného orgánu", PepCategory.StateEnterpriseBoard, "právnická osoba ustanovená štátom"),
        ("dozorného orgánu", PepCategory.StateEnterpriseBoard, "právnická osoba ustanovená štátom"),
        ("dozornej rady", PepCategory.StateEnterpriseBoard, "právnická osoba ustanovená štátom"),
        ("správnej rady", PepCategory.StateEnterpriseBoard, "právnická osoba ustanovená štátom"),
        ("exportno-importnej banky", PepCategory.StateEnterpriseBoard, "Exportno-importná banka Slovenskej republiky"),
        ("železníc slovenskej republiky", PepCategory.StateEnterpriseBoard, "Železnice Slovenskej republiky"),
    ];

    public static DeclaredFunction Classify(string declared)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(declared);

        var match = RoleAndOrganisation().Match(declared);
        var role = Clean(match.Success ? match.Groups["role"].Value : declared);
        var organisation = match.Success ? Clean(match.Groups["org"].Value) : null;

        if (role.Length == 0)
        {
            role = declared.Trim();
        }

        var lower = role.ToLowerInvariant();
        foreach (var (phrase, category, institution) in Rules)
        {
            if (lower.Contains(phrase, StringComparison.Ordinal))
            {
                return new DeclaredFunction(role, organisation, category, organisation ?? institution);
            }
        }

        return new DeclaredFunction(role, organisation, PepCategory.OtherProminentFunction, organisation ?? role);
    }

    private static string Clean(string text) =>
        string.Join(' ', text.Replace(' ', ' ').Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
