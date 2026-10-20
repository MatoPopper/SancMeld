namespace PepMeld.Core.Domain;

/// <summary>The jurisdictions covered. A check without a country searches both.</summary>
public enum Country
{
    SK,
    CZ,
}

/// <summary>
/// One vocabulary of prominent public functions over both national AML laws, so that a
/// finding can be read without knowing which letter of which paragraph it came from.
/// <see cref="LegalBasis"/> gives the paragraph; this says what the function is.
///
/// The Slovak letters are § 6 ods. 2 zák. č. 297/2008 Z. z.; the Czech list is one
/// paragraph, § 4 odst. 5 písm. a) zák. č. 253/2008 Sb., with the same functions and two
/// more: regional leaders and deputies of central-office heads. From 10 July 2027 the
/// categories of Regulation (EU) 2024/1624, article 2(1)(34), apply directly; they map
/// onto these one to one, see <see cref="LegalBasis.Amlr"/>.
/// </summary>
public enum PepCategory
{
    /// <summary>SK a), CZ, AMLR a): head of state.</summary>
    HeadOfState,

    /// <summary>SK a): prime minister, deputy, minister, head of a central state office, state secretary. CZ adds their deputies.</summary>
    GovernmentMember,

    /// <summary>SK b), CZ, AMLR b): member of parliament or a similar legislative body.</summary>
    MemberOfParliament,

    /// <summary>SK c), CZ, AMLR d): judges of supreme, constitutional and other high courts.</summary>
    SeniorJudge,

    /// <summary>SK d), CZ, AMLR e): members of the central bank's board.</summary>
    CentralBankBoard,

    /// <summary>SK d), AMLR e): members of a court of auditors.</summary>
    CourtOfAuditors,

    /// <summary>SK e), CZ, AMLR f): ambassadors, chargés d'affaires, heads of diplomatic missions.</summary>
    Ambassador,

    /// <summary>SK f), CZ, AMLR f): high-ranking officers of the armed forces and security corps.</summary>
    SeniorOfficer,

    /// <summary>SK g), CZ, AMLR g): members of the managing, supervisory or control bodies of state enterprises and state-owned companies.</summary>
    StateEnterpriseBoard,

    /// <summary>SK h): the prosecutor general, regional and district prosecutors and their deputies.</summary>
    Prosecutor,

    /// <summary>SK j), CZ, AMLR c): members of the governing body of a political party or movement.</summary>
    PartyLeadership,

    /// <summary>CZ, AMLR h): heads of regional and local self-government. SK only through i), "a similar function of regional significance".</summary>
    RegionalLeader,

    /// <summary>SK i), CZ: a similar function in an EU institution or an international organisation.</summary>
    EuOrInternational,

    /// <summary>
    /// SK i), CZ "obdobná funkce", AMLR i): another prominent public function of national
    /// significance that no letter names: an ombudsman, the head of a regulator, the
    /// board of a public broadcaster or a social-insurance institution. The Slovak
    /// register of public officials lists many such offices; they are reported under the
    /// role they carry rather than squeezed into the nearest letter.
    /// </summary>
    OtherProminentFunction,
}

public enum CheckStatus
{
    Running,
    Completed,
    Failed,
}

/// <summary>The three stages of a check, in the order they run.</summary>
public enum CheckStage
{
    /// <summary>The PEP lists this instance holds.</summary>
    Lists,

    /// <summary>Boards of state enterprises and state-owned companies, read from the company registers.</summary>
    Boards,

    /// <summary>Family members and close associates.</summary>
    Family,
}

public enum StageStatus
{
    Pending,
    Running,
    Completed,
    Failed,

    /// <summary>Not run: outside the check's scope, or not available in this version. The stage's note says which.</summary>
    Skipped,
}

public enum CheckOutcome
{
    /// <summary>Holds a prominent public function now.</summary>
    Pep,

    /// <summary>Held one that has ended. Each finding says how long ago; the twelve months of SK § 12 ods. 3 are the caller's to apply.</summary>
    FormerPep,

    FamilyMember,
    CloseAssociate,

    /// <summary>Nothing in any list that was searched. The coverage says which lists those were.</summary>
    NoMatch,

    /// <summary>A name matched in a source that publishes no date of birth, so the match is neither confirmed nor excluded.</summary>
    Unverified,
}

public enum FindingKind
{
    Function,
    BoardSeat,
    FamilyMember,
    CloseAssociate,
}

/// <summary>
/// How well a finding is evidenced. The scale is SancMeld's: A is a name and a date of
/// birth from a source that publishes both; B a name from a source without the date, plus
/// a second detail that agrees; C a name alone in an official source; D a name alone in
/// the press or from a language model. A family member never scores above the tie it was
/// found through.
/// </summary>
public enum EvidenceLevel
{
    A,
    B,
    C,
    D,
}

public enum FamilyRelation
{
    Spouse,
    Partner,
    Child,
    Parent,
    Sibling,
    ChildInLaw,
}

public enum ImportStatus
{
    Running,
    Success,
    SkippedUnchanged,
    Failed,
}
