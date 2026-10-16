using SancMeld.Core.Domain;
using SancMeld.Core.Relations;

namespace SancMeld.Core.Search;

/// <summary>
/// What a compliance officer knows about the person in front of them. Only the name is
/// required — everything else sharpens the answer without being demanded, because in
/// practice the officer often has nothing else.
/// </summary>
public sealed record CheckQuery
{
    public required string Name { get; init; }

    /// <summary>
    /// Full date if known. Only the year is compared: publishers routinely disagree about
    /// the day and month for the same person, so comparing them would reject real matches.
    /// </summary>
    public DateOnly? DateOfBirth { get; init; }

    /// <summary>Use this when only the year is known.</summary>
    public short? BirthYear { get; init; }

    /// <summary>ISO 3166-1 alpha-2, e.g. "RU".</summary>
    public string? Country { get; init; }

    /// <summary>Passport, national ID, tax or registration number. Any format.</summary>
    public string? DocumentNumber { get; init; }

    public SubjectType? SubjectType { get; init; }

    /// <summary>The officer's own case or transaction reference. Stored with the check.</summary>
    public string? Reference { get; init; }

    public string? RequestedBy { get; init; }
}

/// <summary>
/// What the officer has to do next. Three answers, not a number — a percentage tells
/// someone who has to sign off nothing they can act on.
/// </summary>
public enum CheckOutcome
{
    /// <summary>Nothing on any list resembles this name.</summary>
    NoMatch,

    /// <summary>Something resembles it. A person has to look.</summary>
    ReviewRequired,

    /// <summary>A document number matched exactly. That is not a similarity judgement.</summary>
    IdentifierMatch,
}

/// <summary>
/// Why a group of subjects is grouped, in terms of evidence rather than a threshold.
///
/// The order is the order of certainty, and the third band is the one this domain is
/// really about: most listings carry no date of birth and no document, so most of the
/// time the honest answer is "the list cannot confirm or deny this" — which is a
/// different thing from a low score, and calls for a different action.
/// </summary>
public enum CheckBand
{
    /// <summary>A document number matched exactly.</summary>
    IdentifierMatch,

    /// <summary>The name matches and something the officer supplied agrees with the listing.</summary>
    Corroborated,

    /// <summary>The name matches and the listing knows nothing that could confirm or deny it.</summary>
    Unverified,

    /// <summary>The name matches but the listing states something that disagrees.</summary>
    Contradicted,
}

public sealed record CheckResult
{
    /// <summary>Identifies the stored check. An adjudication will attach to this.</summary>
    public required Guid CheckId { get; init; }

    public required string Query { get; init; }
    public string? Reference { get; init; }
    public required DateTimeOffset CheckedAt { get; init; }

    public required CheckOutcome Outcome { get; init; }

    /// <summary>One sentence, safe to show or to read aloud.</summary>
    public required string Summary { get; init; }

    public IReadOnlyList<CheckGroup> Groups { get; init; } = [];

    /// <summary>
    /// What the officer supplied, echoed back. Useful in the audit trail and to a person
    /// re-reading the check months later, who will not remember what was typed.
    /// </summary>
    public required CheckInputs Inputs { get; init; }

    /// <summary>
    /// Parties no list names but an open register ties to a listed subject: the company
    /// a sanctioned person owns, the foundation they chair. Beside the groups, never in
    /// them — a listing is a legal fact and this is a register's assertion, matched by a
    /// machine, for a person to confirm. Advisory by construction, and it does not move
    /// <see cref="Outcome"/>.
    /// </summary>
    public IReadOnlyList<CheckRelatedParty> RelatedParties { get; init; } = [];
}

/// <summary>A related party whose name resembles the query, with the listed subjects it is tied to.</summary>
public sealed record CheckRelatedParty
{
    public required string Name { get; init; }
    public required string MatchedName { get; init; }
    public required double NameScore { get; init; }
    public required RelatedPartyKind Kind { get; init; }
    public string? Description { get; init; }
    public string? CountryIso2 { get; init; }
    public IReadOnlyList<RelatedPartyIdentifierView> Identifiers { get; init; } = [];

    /// <summary>The register and the record in it, so the reader can open it.</summary>
    public required string System { get; init; }
    public required string ExternalId { get; init; }
    public required string Url { get; init; }
    public required DateTimeOffset RetrievedAt { get; init; }

    public IReadOnlyList<CheckRelation> Relations { get; init; } = [];
}

/// <summary>One register statement tying the party to one listed subject, with the evidence for both ends.</summary>
public sealed record CheckRelation
{
    public required RelationKind Kind { get; init; }
    public required LinkStatus Status { get; init; }

    /// <summary>The listed subject, as the sanctions list has them.</summary>
    public required string SubjectName { get; init; }
    public required SubjectType SubjectType { get; init; }
    public required Guid DesignationId { get; init; }
    public required string Source { get; init; }
    public required LegalEffect LegalEffect { get; init; }
    public required DesignationStatus ListingStatus { get; init; }
    public DateOnly? ListedAt { get; init; }

    /// <summary>How the listed subject was tied to the register record: the matched spellings, the score, the birth date.</summary>
    public required LinkStatus IdentityStatus { get; init; }
    public required string IdentityUrl { get; init; }
    public string? MatchedName { get; init; }
    public string? MatchedLabel { get; init; }
    public double? NameScore { get; init; }
    public required bool BirthDateMatched { get; init; }

    public decimal? Share { get; init; }
    public DateOnly? StartDate { get; init; }
    public DateOnly? EndDate { get; init; }

    /// <summary>What the register cites. Often nothing, which the reader should see.</summary>
    public IReadOnlyList<string> EvidenceUrls { get; init; } = [];

    /// <summary>One sentence, safe to show.</summary>
    public required string Explanation { get; init; }
}

public sealed record CheckInputs(
    string Name, short? BirthYear, string? Country, bool DocumentSupplied, SubjectType? SubjectType);

public sealed record CheckGroup
{
    public required CheckBand Band { get; init; }

    /// <summary>Plain language: what this group of subjects has in common.</summary>
    public required string Meaning { get; init; }

    /// <summary>Plain language: what the officer should do about it.</summary>
    public required string Action { get; init; }

    /// <summary>How many subjects fall in this band in total, before any cap.</summary>
    public required int TotalCount { get; init; }

    /// <summary>
    /// The subjects themselves. Fewer than <see cref="TotalCount"/> when a band was
    /// capped — never silently: the count above is always the real one.
    /// </summary>
    public IReadOnlyList<CheckSubject> Subjects { get; init; } = [];
}

/// <summary>
/// One human or organisation, with every list it appears on gathered underneath.
///
/// Gathering matters: the same person is listed separately by the EU and the UN, and
/// showing that as two hits reads like a duplicate. It is the opposite — two independent
/// publishers naming the same subject.
/// </summary>
public sealed record CheckSubject
{
    public required string Name { get; init; }

    /// <summary>The spelling that matched. Often an alias, sometimes in another script.</summary>
    public required string MatchedName { get; init; }

    public required SubjectType SubjectType { get; init; }

    /// <summary>Every list this subject appears on.</summary>
    public IReadOnlyList<CheckListing> Listings { get; init; } = [];

    /// <summary>What the lists know about them — to compare against your own records.</summary>
    public required CheckKnownDetails Known { get; init; }

    public required CheckEvidence Evidence { get; init; }
}

public sealed record CheckListing(
    string Source,
    string SourceRecordKey,
    LegalEffect LegalEffect,
    DesignationStatus Status,
    DateOnly? ListedAt,
    IReadOnlyList<string> Programmes,
    string? LegalActTitle,
    string? LegalActUrl,
    Guid DesignationId);

/// <summary>
/// The details the lists actually carry. Deliberately includes the empties: "no date of
/// birth on any list" is the single most useful thing an officer can be told, because it
/// says the list cannot settle this and their own records have to.
/// </summary>
public sealed record CheckKnownDetails(
    IReadOnlyList<string> DatesOfBirth,
    IReadOnlyList<string> PlacesOfBirth,
    IReadOnlyList<string> Nationalities,
    IReadOnlyList<string> Documents,
    IReadOnlyList<string> Addresses);

/// <summary>
/// Each thing the officer supplied, answered separately. Structured so a UI can colour it
/// and so a language model summarising the check has facts to quote rather than infer.
/// </summary>
public sealed record CheckEvidence
{
    public required bool DocumentMatched { get; init; }
    public required QualifierState BirthYear { get; init; }
    public required QualifierState Country { get; init; }
    public required QualifierState SubjectType { get; init; }

    /// <summary>Sentences explaining the states above. Safe to display verbatim.</summary>
    public IReadOnlyList<string> Notes { get; init; } = [];
}
