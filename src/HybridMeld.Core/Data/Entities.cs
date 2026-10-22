using HybridMeld.Core.Domain;

namespace HybridMeld.Core.Data;

/// <summary>
/// A case: what a reader of the workspace sees as "a current hybrid threat". The
/// principal entity; actors are tied to it through claims, never directly.
/// </summary>
public class Campaign
{
    public Guid Id { get; set; }

    /// <summary>URL-safe, unique, chosen once: "voice-of-europe". Never regenerated from the title, so a link keeps working after a rename.</summary>
    public required string Slug { get; set; }
    public required string Title { get; set; }

    /// <summary>A few sentences in Slovak: what happened, who says so, what is known.</summary>
    public required string Summary { get; set; }

    public CampaignKind Kind { get; set; }
    public CampaignStatus Status { get; set; } = CampaignStatus.Active;
    public EditorialStatus Editorial { get; set; } = EditorialStatus.Draft;

    /// <summary>ISO 3166-1 alpha-2, the countries the case plays out in: ["SK","CZ"].</summary>
    public List<string> Countries { get; set; } = [];

    public DateOnly? StartedOn { get; set; }
    public DateOnly? EndedOn { get; set; }

    /// <summary>Who the case is attributed to, when the curator commits to one actor; the claims carry the evidence for it.</summary>
    public Guid? AttributedToActorId { get; set; }
    public Actor? AttributedTo { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public string? WithdrawnReason { get; set; }

    public List<Claim> Claims { get; set; } = [];
}

/// <summary>
/// A person, an organisation, a website, a channel or a unit. One row per real-world
/// thing across every case; the same website in two cases is one actor with two sets of
/// claims. A private person is named here on the curator's responsibility and with the
/// curator's name on the row.
/// </summary>
public class Actor
{
    public Guid Id { get; set; }
    public ActorKind Kind { get; set; }

    public required string Name { get; set; }

    /// <summary>SancMeld's normal form (NameNormalizer), the column the search reads.</summary>
    public required string NameNormalized { get; set; }

    /// <summary>ISO 3166-1 alpha-2: registration, nationality or the country the channel addresses.</summary>
    public string? Country { get; set; }

    /// <summary>{"domain": "voiceofeurope.com", "telegram": "@…", "ico": "…", "wikidata": "Q…"} as JSON; whatever identifies the actor outside a name.</summary>
    public string? IdentifiersJson { get; set; }

    /// <summary>A few words: "former MEP", "GRU unit", "news site in Prague".</summary>
    public string? Description { get; set; }

    public bool IsPublicFigure { get; set; }

    /// <summary>Why a private person is named at all; recommended, not required, and shown next to the name.</summary>
    public string? PublicRoleJustification { get; set; }

    // Soft, one-way links to the compliance services. No foreign key across databases;
    // a nightly job checks the target still exists and refreshes the cached name.
    public Guid? SanctionDesignationId { get; set; }
    public string? SanctionSourceCode { get; set; }
    public string? SanctionCachedName { get; set; }

    public Guid? PepPersonId { get; set; }
    public string? PepSourceCode { get; set; }
    public string? PepCachedName { get; set; }

    public DateTimeOffset? LinkVerifiedAt { get; set; }

    /// <summary>Set when the verification found the target gone or delisted; the screen says so instead of hiding the link.</summary>
    public DateTimeOffset? LinkStaleAt { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<ActorAlias> Aliases { get; set; } = [];
}

/// <summary>Another name of the same actor: APT28 for GRU unit 26165, with where the alias comes from.</summary>
public class ActorAlias
{
    public long Id { get; set; }
    public Guid ActorId { get; set; }
    public Actor? Actor { get; set; }

    public required string Name { get; set; }
    public required string NameNormalized { get; set; }

    /// <summary>analyst | misp | mitre | sanctions.</summary>
    public required string Origin { get; set; }
    public string? SourceUrl { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One sentence about one actor in one case: subject, verb, optional object. The core
/// of the workspace and the edge of the graph. It has at least one piece of evidence with
/// a verbatim quote, or it is not saved.
/// </summary>
public class Claim
{
    public long Id { get; set; }
    public Guid CampaignId { get; set; }
    public Campaign? Campaign { get; set; }

    public Guid SubjectActorId { get; set; }
    public Actor? Subject { get; set; }

    public Predicate Predicate { get; set; }

    public Guid? ObjectActorId { get; set; }
    public Actor? Object { get; set; }

    /// <summary>The sentence as the reader sees it, in Slovak, not beyond what the evidence says.</summary>
    public required string Statement { get; set; }

    public EvidenceLevel Level { get; set; }
    public ClaimStatus Status { get; set; } = ClaimStatus.Candidate;
    public FoundBy FoundBy { get; set; }

    public DateOnly? OccurredFrom { get; set; }
    public DateOnly? OccurredTo { get; set; }

    /// <summary>The inbox item the claim was made from, when it was; the item is then Linked.</summary>
    public Guid? SourceItemId { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }
    public string? DecisionNote { get; set; }

    public List<Evidence> Evidence { get; set; } = [];
}

/// <summary>
/// Where a claim comes from: the URL, the publisher, and the verbatim quote. The quote
/// is mandatory and the page is kept as a snapshot, because a channel deletes its posts
/// and a journalist's finding without its source is worth nothing a month later.
/// </summary>
public class Evidence
{
    public long Id { get; set; }
    public long ClaimId { get; set; }
    public Claim? Claim { get; set; }

    public required string Url { get; set; }
    public string? ArchiveUrl { get; set; }
    public string? Publisher { get; set; }
    public string? Title { get; set; }
    public required string Quote { get; set; }
    public DateOnly? PublishedOn { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }

    /// <summary>Where the page as fetched is kept; null when the fetch failed, and the row says so rather than pretending.</summary>
    public string? SnapshotPath { get; set; }
    public string? Sha256 { get; set; }
    public string? Language { get; set; }

    public required string AddedBy { get; set; }
}

/// <summary>Every decision taken on a claim, in order; the claim holds the latest, this holds them all.</summary>
public class ClaimDecision
{
    public long Id { get; set; }
    public long ClaimId { get; set; }
    public ClaimStatus PreviousStatus { get; set; }
    public ClaimStatus Decision { get; set; }
    public string? Note { get; set; }
    public required string DecidedBy { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}

/// <summary>
/// A source the workspace watches: an official RSS feed, a Telegram channel, a website.
/// Any user with the hybrid grant may add one; who did is on the row. Unverified sources
/// are welcome here, the level on each claim says how far to trust what came out of them.
/// </summary>
public class Source
{
    public Guid Id { get; set; }

    /// <summary>Short and unique, upper-case: "EU_COUNCIL_PRESS", "TG_SOMECHANNEL".</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public string? Publisher { get; set; }
    public required string Url { get; set; }

    public SourceKind Kind { get; set; }
    public FetchKind FetchKind { get; set; }
    public bool IsOfficial { get; set; }
    public string? Licence { get; set; }

    [System.ComponentModel.DataAnnotations.Range(1, 24 * 365)]
    public int RefreshHours { get; set; } = 24;
    public bool IsEnabled { get; set; } = true;

    public required string AddedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>One fetch of one source: when, what it found, what went wrong.</summary>
public class SourceRun
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Source? Source { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public RunStatus Status { get; set; }
    public int ItemsSeen { get; set; }
    public int ItemsNew { get; set; }
    public string? Error { get; set; }
}

/// <summary>
/// One post, article or entry a fetch brought in, waiting in the inbox to be read, linked
/// to a claim or ignored. Kept with a snapshot of its text, since the original may go.
/// </summary>
public class SourceItem
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public Source? Source { get; set; }

    /// <summary>The post's own id or URL in the source, unique per source, so a refetch does not duplicate it.</summary>
    public required string ExternalId { get; set; }
    public required string Url { get; set; }
    public string? Title { get; set; }

    /// <summary>The text as fetched, capped; the full page, when kept, is at <see cref="SnapshotPath"/>.</summary>
    public string? Text { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public DateTimeOffset FetchedAt { get; set; }
    public string? Sha256 { get; set; }
    public string? SnapshotPath { get; set; }

    public SourceItemStatus Status { get; set; } = SourceItemStatus.New;

    /// <summary>What an extraction (later, an agent) pulled out: names, organisations, domains, as JSON. Never a claim by itself.</summary>
    public string? ExtractedJson { get; set; }

    public string? TriagedBy { get; set; }
    public DateTimeOffset? TriagedAt { get; set; }
    public string? TriageNote { get; set; }
}

/// <summary>Who may sign in: the address, its display name, and whether it is blocked here. The grant itself is read from SancMeld's api_user.</summary>
public class ApiUser
{
    public Guid Id { get; set; }
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? GoogleSubject { get; set; }
    public bool IsBlocked { get; set; }
    public string? BlockedBy { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

/// <summary>A program's key, acting as its owner. Kept for the day an agent writes here; none is issued today.</summary>
public class ApiKey
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string OwnerEmail { get; set; }
    public required string KeyHash { get; set; }
    public required string DisplayPrefix { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// A question worth watching that is not a case: no actor is accused of anything, there
/// are public facts (indicators) and two lists saying what would confirm and what would
/// refute it. Kept apart from campaigns on purpose (user's decision of 2026-10-22): a
/// hypothesis has no claims, puts no edge in the graph and names related actors only as
/// a soft list, so a water utility whose contract is public never appears next to a GRU
/// unit as if something were established.
/// </summary>
public class Hypothesis
{
    public Guid Id { get; set; }
    public required string Slug { get; set; }
    public required string Title { get; set; }

    /// <summary>One sentence, a question: "Do published technical annexes make mapping the resilience of water utilities easier?"</summary>
    public required string Question { get; set; }

    /// <summary>Why it is worth asking, in a few sentences, and what is explicitly not claimed.</summary>
    public required string Rationale { get; set; }

    public List<string> Countries { get; set; } = [];
    public HypothesisStatus Status { get; set; } = HypothesisStatus.Open;

    /// <summary>What an analyst waits for: each line one observation that would turn the question into a case.</summary>
    public List<string> ConfirmWhen { get; set; } = [];
    public List<string> RefuteWhen { get; set; } = [];

    /// <summary>Editorial care: what not to link or reproduce (technical annexes of critical infrastructure, private persons).</summary>
    public string? HandlingNote { get; set; }

    public string? PromotedToSlug { get; set; }
    public string? DecisionNote { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public required string CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    public List<Indicator> Indicators { get; set; } = [];
    public List<HypothesisActor> Actors { get; set; } = [];
}

/// <summary>
/// One public fact behind a hypothesis, with its source and verbatim quote: the same
/// discipline as evidence on a claim, but the statement is descriptive ("a contract was
/// published", "an authority warned"), never a verb between two actors.
/// </summary>
public class Indicator
{
    public long Id { get; set; }
    public Guid HypothesisId { get; set; }
    public Hypothesis? Hypothesis { get; set; }

    public required string Statement { get; set; }
    public EvidenceLevel Level { get; set; }
    public DateOnly? ObservedOn { get; set; }

    public required string Url { get; set; }
    public string? ArchiveUrl { get; set; }
    public string? Publisher { get; set; }
    public string? Title { get; set; }
    public required string Quote { get; set; }
    public DateOnly? PublishedOn { get; set; }
    public string? Language { get; set; }
    public DateTimeOffset RetrievedAt { get; set; }
    public string? SnapshotPath { get; set; }
    public string? Sha256 { get; set; }
    public required string AddedBy { get; set; }
}

/// <summary>An actor a hypothesis is about, as a pointer only: no claim, no edge, no attribution.</summary>
public class HypothesisActor
{
    public Guid HypothesisId { get; set; }
    public Hypothesis? Hypothesis { get; set; }
    public Guid ActorId { get; set; }
    public Actor? Actor { get; set; }
    public required string AddedBy { get; set; }
    public DateTimeOffset AddedAt { get; set; }
}

/// <summary>Every change of a hypothesis' status, in order, as ClaimDecision keeps a claim's.</summary>
public class HypothesisDecision
{
    public long Id { get; set; }
    public Guid HypothesisId { get; set; }
    public HypothesisStatus PreviousStatus { get; set; }
    public HypothesisStatus Decision { get; set; }
    public string? Note { get; set; }
    public string? PromotedToSlug { get; set; }
    public required string DecidedBy { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
}
