namespace HybridMeld.Core.Domain;

/// <summary>
/// What a case is about. One case, one kind: a disinformation operation that also ran a
/// hack is two cases that share actors, and the graph shows that better than a flag.
/// </summary>
public enum CampaignKind
{
    /// <summary>Foreign information manipulation and interference: fake media, paid amplification, forged documents.</summary>
    Fimi,
    Cyber,
    Sabotage,
    ElectionInterference,
    Espionage,

    /// <summary>Paid intermediaries and agents of influence: the politician on a payroll, the "think-tank" with one donor.</summary>
    ProxyInfluence,
    EconomicCoercion,
    MigrationInstrumentalisation,
    Other,
}

public enum CampaignStatus { Active, Dormant, Closed }

/// <summary>Draft is the curator's desk and only they see it; Published is what every reader sees; Withdrawn stays, greyed, with its reason.</summary>
public enum EditorialStatus { Draft, Published, Withdrawn }

public enum ActorKind { Person, Organisation, Website, Channel, MilitaryUnit, StateBody }

/// <summary>
/// What one claim says the subject did to, or is to, the object. The verb is the edge of
/// the graph. "Defends" is here because the first case that reached a politician was one
/// who defended a sanctioned website in public: an indirect tie, level D, and a sentence
/// a reader should see as exactly that.
/// </summary>
public enum Predicate
{
    Operates,
    Controls,
    Funds,
    FundedBy,
    Amplifies,
    Defends,
    AppearsOn,
    MemberOf,
    EmployedBy,
    AttributedTo,
    TargetOf,
    HostedBy,
    RegisteredBy,
    SameAs,

    /// <summary>The seed claim: the subject is listed under a sanctions regime. Object is none; the evidence is the act.</summary>
    ListedUnder,
}

/// <summary>
/// How strong what the evidence says is, as a label and never as a gate (the user's
/// decision of 2026-10-21: the workspace does not follow AML law, and its readers are
/// analysts and journalists who weigh sources themselves).
///
/// A: an official act — a sanctions regulation with its reasons, a court ruling, a report
/// of a national security authority, a government or Council statement.
/// B: a report of a state-established body or a major platform (EUvsDisinfo, Viginum,
/// Meta, Microsoft), or investigative journalism with documents.
/// C: media or a think-tank without documents; a name match in a register without a date
/// of birth.
/// D: indirect evidence, an inference of the analyst or the agent.
/// </summary>
public enum EvidenceLevel { A, B, C, D }

/// <summary>
/// Confirmed means a person checked that the quote is in the source and the sentence does
/// not go beyond it, not that the claim is true. Candidate is unchecked, Rejected was
/// checked and found wanting, Withdrawn was right and is no longer shown as current.
/// </summary>
public enum ClaimStatus { Candidate, Confirmed, Rejected, Withdrawn }

public enum FoundBy { Analyst, Agent, OfficialAct }

public enum SourceKind { Official, Platform, ThinkTank, Media, Structured, Channel, Account, Forum }

/// <summary>How a watched source is read. Telegram is the public web mirror t.me/s/… and needs no key; X has no free read and is Manual.</summary>
public enum FetchKind { Rss, Html, Telegram, Manual }

public enum SourceItemStatus { New, Triaged, Linked, Ignored }

public enum RunStatus { Running, Success, Failed }

/// <summary>
/// Open is a question being watched; Promoted means a case was opened from it (the slug
/// is on the row); Dismissed means the question was closed with a reason. A hypothesis
/// never becomes a case by changing a flag: the case is written separately, with actors
/// and claims, and the hypothesis points at it.
/// </summary>
public enum HypothesisStatus { Open, Promoted, Dismissed }
