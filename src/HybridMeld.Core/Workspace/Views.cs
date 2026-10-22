using System.Text.Json;
using HybridMeld.Core.Domain;

namespace HybridMeld.Core.Workspace;

// What the API answers and what it accepts. One file, so the contract the front end
// codes against is readable in one place.

/// <summary>An actor as a claim or a list names it: enough to draw a chip and to say whether it reaches the compliance services.</summary>
public sealed record ActorRef(
    Guid Id,
    ActorKind Kind,
    string Name,
    string? Country,
    bool IsPublicFigure,
    Guid? SanctionDesignationId,
    string? SanctionSourceCode,
    Guid? PepPersonId,
    string? PepSourceCode,
    bool LinkStale);

public sealed record EvidenceView(
    long Id,
    string Url,
    string? ArchiveUrl,
    string? Publisher,
    string? Title,
    string Quote,
    DateOnly? PublishedOn,
    DateTimeOffset RetrievedAt,
    bool HasSnapshot,
    string? Language,
    string AddedBy);

public sealed record ClaimView(
    long Id,
    Guid CampaignId,
    string CampaignSlug,
    string CampaignTitle,
    ActorRef Subject,
    Predicate Predicate,
    ActorRef? Object,
    string Statement,
    EvidenceLevel Level,
    ClaimStatus Status,
    FoundBy FoundBy,
    DateOnly? OccurredFrom,
    DateOnly? OccurredTo,
    Guid? SourceItemId,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? DecidedBy,
    DateTimeOffset? DecidedAt,
    string? DecisionNote,
    IReadOnlyList<EvidenceView> Evidence);

public sealed record CampaignSummary(
    Guid Id,
    string Slug,
    string Title,
    string Summary,
    CampaignKind Kind,
    CampaignStatus Status,
    EditorialStatus Editorial,
    IReadOnlyList<string> Countries,
    DateOnly? StartedOn,
    DateOnly? EndedOn,
    ActorRef? AttributedTo,
    int ClaimCount,
    int ConfirmedCount,
    int ActorCount,
    bool TouchesSanctions,
    bool TouchesPep,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? PublishedAt,
    string? WithdrawnReason);

public sealed record CampaignView(
    CampaignSummary Campaign,
    IReadOnlyList<ActorRef> Actors,
    IReadOnlyList<ClaimView> Claims);

public sealed record AliasView(long Id, string Name, string Origin, string? SourceUrl);

public sealed record ActorView(
    ActorRef Actor,
    string? Description,
    JsonElement? Identifiers,
    string? PublicRoleJustification,
    string? SanctionCachedName,
    string? PepCachedName,
    DateTimeOffset? LinkVerifiedAt,
    DateTimeOffset? LinkStaleAt,
    IReadOnlyList<AliasView> Aliases,
    IReadOnlyList<CampaignSummary> Campaigns,
    IReadOnlyList<ClaimView> Claims,
    IReadOnlyList<HypothesisSummary> Hypotheses,
    string CreatedBy,
    DateTimeOffset CreatedAt);

public sealed record ActorHit(ActorRef Actor, string? Description, string? MatchedAlias, int ClaimCount);

public sealed record Page<T>(IReadOnlyList<T> Items, int Total);

/// <summary>What is new since a moment: the "current threats" screen.</summary>
public sealed record FeedView(DateTimeOffset Since, IReadOnlyList<CampaignSummary> Campaigns, IReadOnlyList<ClaimView> Claims);

public sealed record SourceView(
    Guid Id,
    string Code,
    string Name,
    string? Publisher,
    string Url,
    SourceKind Kind,
    FetchKind FetchKind,
    bool IsOfficial,
    string? Licence,
    int RefreshHours,
    bool IsEnabled,
    string AddedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset? LastRunAt,
    RunStatus? LastRunStatus,
    string? LastError,
    int NewItems);

public sealed record SourceItemView(
    Guid Id,
    Guid SourceId,
    string SourceCode,
    string SourceName,
    string ExternalId,
    string Url,
    string? Title,
    string? Text,
    DateTimeOffset? PublishedAt,
    DateTimeOffset FetchedAt,
    SourceItemStatus Status,
    string? TriagedBy,
    DateTimeOffset? TriagedAt,
    string? TriageNote,
    int LinkedClaims);

public sealed record FetchOutcome(string SourceCode, RunStatus Status, int ItemsSeen, int ItemsNew, TimeSpan Duration, string? Error);

// ---- inputs ----

public sealed record CampaignInput(
    string Title,
    string Summary,
    CampaignKind Kind,
    CampaignStatus Status,
    IReadOnlyList<string>? Countries,
    DateOnly? StartedOn,
    DateOnly? EndedOn,
    Guid? AttributedToActorId,
    // Chosen on creation and kept; ignored on update. Derived from the title when empty.
    string? Slug);

public sealed record EditorialInput(EditorialStatus Editorial, string? Reason);

public sealed record ActorInput(
    ActorKind Kind,
    string Name,
    string? Country,
    JsonElement? Identifiers,
    string? Description,
    bool IsPublicFigure,
    string? PublicRoleJustification,
    IReadOnlyList<string>? Aliases);

/// <summary>Sets or clears the one-way links to a sanctions record and a PEP person. The names are looked up and cached when the databases are reachable.</summary>
public sealed record ActorLinkInput(Guid? SanctionDesignationId, Guid? PepPersonId);

public sealed record EvidenceInput(
    string Url,
    string Quote,
    string? Publisher,
    string? Title,
    DateOnly? PublishedOn,
    string? Language,
    string? ArchiveUrl);

public sealed record ClaimInput(
    Guid CampaignId,
    Guid SubjectActorId,
    Predicate Predicate,
    Guid? ObjectActorId,
    string Statement,
    EvidenceLevel Level,
    DateOnly? OccurredFrom,
    DateOnly? OccurredTo,
    // The inbox item this comes from; the item becomes Linked and, with no evidence given, is the evidence.
    Guid? SourceItemId,
    IReadOnlyList<EvidenceInput>? Evidence);

public sealed record ClaimUpdateInput(
    Predicate Predicate,
    Guid? ObjectActorId,
    string Statement,
    EvidenceLevel Level,
    DateOnly? OccurredFrom,
    DateOnly? OccurredTo);

public sealed record ClaimDecisionInput(ClaimStatus Decision, string? Note);

public sealed record SourceInput(
    string? Code,
    string Name,
    string? Publisher,
    string Url,
    SourceKind Kind,
    FetchKind FetchKind,
    bool IsOfficial,
    string? Licence,
    int? RefreshHours,
    bool? IsEnabled);

public sealed record TriageInput(SourceItemStatus Status, string? Note);

/// <summary>A refused request, with a stable code the front end can key a message on. Mapped to 400, 404 or 409 by the endpoints.</summary>
public sealed class WorkspaceException(string code, string message, int status = 400) : Exception(message)
{
    public string Code { get; } = code;
    public int Status { get; } = status;

    public static WorkspaceException NotFound(string what) => new("not_found", $"{what} was not found.", 404);
    public static WorkspaceException Invalid(string code, string message) => new(code, message);
    public static WorkspaceException Conflict(string code, string message) => new(code, message, 409);
}

// ---- hypotheses ----

public sealed record IndicatorView(
    long Id,
    string Statement,
    EvidenceLevel Level,
    DateOnly? ObservedOn,
    string Url,
    string? ArchiveUrl,
    string? Publisher,
    string? Title,
    string Quote,
    DateOnly? PublishedOn,
    bool HasSnapshot,
    string? Language,
    DateTimeOffset RetrievedAt,
    string AddedBy);

public sealed record HypothesisSummary(
    Guid Id,
    string Slug,
    string Title,
    string Question,
    HypothesisStatus Status,
    IReadOnlyList<string> Countries,
    int IndicatorCount,
    int ActorCount,
    bool TouchesSanctions,
    bool TouchesPep,
    string? PromotedToSlug,
    string CreatedBy,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    DateTimeOffset? DecidedAt);

public sealed record HypothesisView(
    HypothesisSummary Hypothesis,
    string Rationale,
    IReadOnlyList<string> ConfirmWhen,
    IReadOnlyList<string> RefuteWhen,
    string? HandlingNote,
    IReadOnlyList<IndicatorView> Indicators,
    IReadOnlyList<ActorRef> Actors,
    string? DecidedBy,
    string? DecisionNote);

public sealed record HypothesisInput(
    string Title,
    string Question,
    string Rationale,
    IReadOnlyList<string>? Countries,
    IReadOnlyList<string>? ConfirmWhen,
    IReadOnlyList<string>? RefuteWhen,
    string? HandlingNote,
    // Chosen on creation and kept; ignored on update. Derived from the title when empty.
    string? Slug);

public sealed record IndicatorInput(string Statement, EvidenceLevel Level, DateOnly? ObservedOn, EvidenceInput Evidence);

/// <summary>The whole list, replacing what was there; an empty list clears it.</summary>
public sealed record HypothesisActorsInput(IReadOnlyList<Guid> ActorIds);

/// <summary>Promoted needs the slug of the case written from it; Dismissed needs a note; Open takes either back.</summary>
public sealed record HypothesisDecisionInput(HypothesisStatus Decision, string? Note, string? PromotedToSlug);
