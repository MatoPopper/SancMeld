using SancMeld.Core.Domain;

namespace SancMeld.Core.Relations;

/// <summary>
/// What a node or an edge of the graph is, in the terms an officer reads it by.
///
/// The four colours of the legend. Confirmed is what a publisher declared or an analyst
/// signed off; Candidate is what a machine proposed with its evidence attached; Review is
/// a candidate that reaches a binding listing or a controlling share, the one a person
/// has to look at today; Rejected is kept on the drawing greyed, so the reader sees it
/// was considered.
/// </summary>
public enum GraphStatus { Subject, Confirmed, Candidate, Review, Rejected }

public enum GraphNodeKind { Listing, Party }

public sealed record GraphNode
{
    /// <summary>"d:" + designation id for a listing, "p:" + party id for a register record.</summary>
    public required string Id { get; init; }
    public required GraphNodeKind Kind { get; init; }
    public required string Label { get; init; }
    public required GraphStatus Status { get; init; }

    public Guid? DesignationId { get; init; }
    public string? Source { get; init; }
    public LegalEffect? LegalEffect { get; init; }
    public SubjectType? SubjectType { get; init; }

    public RelatedPartyKind? PartyKind { get; init; }

    /// <summary>
    /// Where the entity is registered or, for a person, their first stated nationality:
    /// an organisation's first address country on a listing, the register's country for
    /// a party. Drawn as a flag; null when nobody records one.
    /// </summary>
    public string? CountryIso2 { get; init; }
    public string? Url { get; init; }

    /// <summary>One line for a tooltip: the role, the description, the listing date.</summary>
    public string? Detail { get; init; }
}

public sealed record GraphEdge
{
    public required string From { get; init; }
    public required string To { get; init; }

    /// <summary>A <see cref="RelationKind"/> name, or cross_reference, same_name, mentions, mentioned_by.</summary>
    public required string Kind { get; init; }
    public required GraphStatus Status { get; init; }

    /// <summary>Why this edge is drawn — the matched spellings, the passage, the reference — one sentence.</summary>
    public required string Evidence { get; init; }
    public IReadOnlyList<string> EvidenceUrls { get; init; } = [];
    public decimal? Share { get; init; }
}

/// <summary>Everything drawn around one subject: the subject itself, what it is tied to, and what ties it.</summary>
public sealed record DesignationGraph
{
    public required Guid SubjectId { get; init; }
    public IReadOnlyList<GraphNode> Nodes { get; init; } = [];
    public IReadOnlyList<GraphEdge> Edges { get; init; } = [];

    /// <summary>True when a cap cut something off; the counts say what the full picture holds.</summary>
    public bool Truncated { get; init; }
}

/// <summary>The listings an import run wrote, each with what else it touches.</summary>
public sealed record RecentListingsView
{
    public IReadOnlyList<RecentRun> Runs { get; init; } = [];
}

public sealed record RecentRun
{
    public required Guid RunId { get; init; }
    public required string Source { get; init; }
    public required LegalEffect LegalEffect { get; init; }
    public required DateTimeOffset StartedAt { get; init; }
    public DateTimeOffset? FinishedAt { get; init; }
    public required int Inserted { get; init; }
    public required int Updated { get; init; }

    /// <summary>New subjects of this run, capped; <see cref="Inserted"/> is the real count.</summary>
    public IReadOnlyList<RecentListing> Listings { get; init; } = [];
}

public sealed record RecentListing
{
    public required Guid DesignationId { get; init; }
    public required string Name { get; init; }
    public required SubjectType SubjectType { get; init; }
    public DateOnly? ListedAt { get; init; }
    public string? Function { get; init; }

    /// <summary>The worst status among everything this listing touches — what colour it gets in a feed.</summary>
    public required GraphStatus Status { get; init; }

    public required ImpactSummary Impact { get; init; }
}

/// <summary>Counts and the first few names of each kind of tie, for a feed row.</summary>
public sealed record ImpactSummary
{
    public required int CrossReferences { get; init; }
    public required int RelatedParties { get; init; }
    public required int MentionedBy { get; init; }
    public required int Mentions { get; init; }

    /// <summary>"owner of Akhmat Fund", "mentioned by SZNEJDER Andriej (PL_MSWIA)"…, a handful.</summary>
    public IReadOnlyList<ImpactLine> Lines { get; init; } = [];
}

public sealed record ImpactLine(string Kind, string Label, GraphStatus Status, Guid? DesignationId, string? Url);
