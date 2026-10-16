using SancMeld.Core.Domain;
using SancMeld.Core.Search;

namespace SancMeld.Core.Relations;

/// <summary>
/// What a listed subject is to a related party, from the listed subject's side. A listed
/// person is the <em>owner of</em> a company; the company is never the subject here.
///
/// Stored as snake_case text, like every enum in the schema; a member's name is part of
/// the stored data.
/// </summary>
public enum RelationKind
{
    OwnerOf,
    OwnedBy,
    FounderOf,
    ChiefExecutiveOf,
    ChairpersonOf,
    BoardMemberOf,
    DirectorOf,
    ParentOrganisationOf,
    SubsidiaryOf,

    /// <summary>Runs the stores or the business of a brand it need not own: ChainStores SK runs MERE in Slovakia.</summary>
    OperatorOf,
}

/// <summary>What an external register says a related party is. Decides which screening a party can answer.</summary>
public enum RelatedPartyKind { Person, Organisation, Vessel, Other }

/// <summary>
/// A link made by a machine is a candidate until a person has looked at it. Rejected is
/// kept rather than deleted: the next run would only make it again.
/// </summary>
public enum LinkStatus { Candidate, Confirmed, Rejected }

/// <summary>External system a related party or an identity comes from. The value stored in the `system` columns.</summary>
public static class RelationSystems
{
    public const string Wikidata = "WIKIDATA";

    /// <summary>
    /// What an analyst entered by hand from articles, rulings and registers: the links
    /// no open register carries, such as a Slovak operating company to the group whose
    /// owners are listed. Every link names its source.
    /// </summary>
    public const string Curated = "CURATED";
}

/// <summary>One related party as a screening sees it.</summary>
public sealed record RelatedPartyView
{
    public required long Id { get; init; }
    public required string System { get; init; }
    public required string ExternalId { get; init; }

    /// <summary>Where a reader can see the record: the item page on Wikidata.</summary>
    public required string Url { get; init; }

    public required string Name { get; init; }
    public string? Description { get; init; }
    public required RelatedPartyKind Kind { get; init; }
    public string? CountryIso2 { get; init; }
    public IReadOnlyList<RelatedPartyIdentifierView> Identifiers { get; init; } = [];
    public IReadOnlyList<string> Names { get; init; } = [];
    public required DateTimeOffset RetrievedAt { get; init; }
}

public sealed record RelatedPartyIdentifierView(string Kind, string Value);

/// <summary>A listed subject's relation to one party, with the listing it rests on.</summary>
public sealed record RelationView
{
    public required long Id { get; init; }
    public required RelationKind Kind { get; init; }
    public required LinkStatus Status { get; init; }

    /// <summary>How the listed subject was tied to the external record — the identity's own status and evidence.</summary>
    public required LinkStatus IdentityStatus { get; init; }
    public required string IdentityExternalId { get; init; }
    public required string IdentityUrl { get; init; }
    public string? MatchedName { get; init; }
    public string? MatchedLabel { get; init; }
    public double? NameScore { get; init; }
    public required bool BirthDateMatched { get; init; }

    public decimal? Share { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }

    /// <summary>The sources the register cites for the statement, when it cites any.</summary>
    public IReadOnlyList<string> EvidenceUrls { get; init; } = [];

    public required DateTimeOffset FirstSeenAt { get; init; }
    public required DateTimeOffset LastSeenAt { get; init; }

    /// <summary>The current listing of the subject on the other end.</summary>
    public required DesignationView Designation { get; init; }

    /// <summary>The party the listed subject is tied to directly — not the searched one when <see cref="Via"/> is set.</summary>
    public string? PartyName { get; init; }

    /// <summary>
    /// The chain from the searched party up to <see cref="PartyName"/>, when the tie is
    /// not direct: ["Hung Trade DOO"… ] reads "ChainStores SK → MERE → Svetofor Group".
    /// Empty for a direct tie.
    /// </summary>
    public IReadOnlyList<string> Via { get; init; } = [];
}

/// <summary>A party whose name resembles the query, with every listed subject it is related to.</summary>
public sealed record RelatedPartyHit
{
    public required RelatedPartyView Party { get; init; }
    public required string MatchedName { get; init; }
    public required double NameScore { get; init; }
    public IReadOnlyList<RelationView> Relations { get; init; } = [];
}

/// <summary>Everything a listed subject is related to, for its detail page.</summary>
public sealed record DesignationRelationsView
{
    public IReadOnlyList<DesignationRelationView> Relations { get; init; } = [];
}

public sealed record DesignationRelationView
{
    public required long Id { get; init; }
    public required long IdentityId { get; init; }
    public required RelatedPartyView Party { get; init; }
    public required RelationKind Kind { get; init; }
    public required LinkStatus Status { get; init; }
    public required LinkStatus IdentityStatus { get; init; }
    public decimal? Share { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }
    public IReadOnlyList<string> EvidenceUrls { get; init; } = [];
    public required DateTimeOffset LastSeenAt { get; init; }
}
