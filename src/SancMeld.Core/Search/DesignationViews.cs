using SancMeld.Core.Domain;

namespace SancMeld.Core.Search;

/// <summary>
/// One listing as it is served.
///
/// Every collection here can legitimately be empty, and most of them often are: on the EU
/// list alone, 2 364 of 6 234 subjects carry no date of birth and 4 245 carry no identifier
/// at all. An incomplete listing is not a lesser listing — the subject is sanctioned either
/// way — so nothing is filtered out for being sparse, and absence is served as an empty
/// collection rather than hidden.
/// </summary>
public sealed record DesignationView
{
    public required Guid Id { get; init; }
    public required string Source { get; init; }
    public required string SourceRecordKey { get; init; }
    public string? ExternalId { get; init; }

    /// <summary>Binding inside the EU, or advisory only — a red flag, never an obligation.</summary>
    public required LegalEffect LegalEffect { get; init; }

    public required SubjectType SubjectType { get; init; }
    public required DesignationStatus Status { get; init; }
    public DateOnly? ListedAt { get; init; }

    /// <summary>
    /// The role the publisher states. The nearest thing to a reason in the structured
    /// export; the narrative justification is in the act linked from LegalBasis.
    /// </summary>
    public string? Function { get; init; }

    /// <summary>
    /// Usually a rank or honorific, but on 122 subjects the publisher puts the role here
    /// instead — sometimes the only one. Show both this and Function.
    /// </summary>
    public string? Title { get; init; }
    public string? Remarks { get; init; }

    /// <summary>When this revision became current. The listing itself may be far older.</summary>
    public required DateTimeOffset ValidFrom { get; init; }

    public IReadOnlyList<NameView> Names { get; init; } = [];
    public IReadOnlyList<BirthView> Births { get; init; } = [];
    public IReadOnlyList<IdentifierView> Identifiers { get; init; } = [];
    public IReadOnlyList<AddressView> Addresses { get; init; } = [];
    public IReadOnlyList<NationalityView> Nationalities { get; init; } = [];
    public IReadOnlyList<MeasureType> Measures { get; init; } = [];
    public IReadOnlyList<string> Programmes { get; init; } = [];
    public IReadOnlyList<CrossReferenceView> CrossReferences { get; init; } = [];

    /// <summary>
    /// The acts this listing rests on, each with its Official Journal link. This is the
    /// difference between "our system says this person is sanctioned" and "this person is
    /// sanctioned by Regulation 2022/1529, here is the text" — only the second is an
    /// answer to a supervisor.
    /// </summary>
    public IReadOnlyList<LegalBasisView> LegalBasis { get; init; } = [];

    /// <summary>Which published file this record came from, and when it was fetched.</summary>
    public required ProvenanceView Provenance { get; init; }
}

public sealed record LegalBasisView(
    string Relation,
    string InstrumentType,
    string? Number,
    string? Title,
    DateOnly? PublicationDate,
    DateOnly? EffectiveDate,
    string? Url,
    string? Celex);

/// <summary>
/// Where the record came from. The checksum and the retrieval time are what make a past
/// decision reconstructible: the exact bytes are kept, and this says which ones they were.
/// </summary>
public sealed record ProvenanceView(
    string SourceName,
    string Publisher,
    string SourceUrl,
    DateTimeOffset RetrievedAt,
    string FileSha256,
    string? PublisherVersion);

public sealed record NameView(string Value, NameType Kind, string? LanguageCode, bool IsWeak);

public sealed record BirthView(
    short? Year, short? Month, short? Day, short? YearFrom, short? YearTo,
    DatePrecision Precision, string? City, string? Region, string? CountryIso2, string? CountryOriginal);

public sealed record IdentifierView(
    IdentifierType Kind, string Value, string? IssuingCountry,
    DateOnly? IssuedAt, DateOnly? ExpiresAt, bool? IsKnownFalse, bool? IsExpired);

public sealed record AddressView(
    string? Street, string? PoBox, string? City, string? PostalCode,
    string? Region, string? CountryIso2, string? CountryOriginal, bool IsPartial);

public sealed record NationalityView(string? CountryIso2, string CountryOriginal);

public sealed record CrossReferenceView(string Kind, string? TargetSourceCode, string TargetExternalId);

/// <summary>
/// A page of results, with the cursor to continue from.
///
/// Keyed rather than numbered: an offset shifts under any import that inserts a record,
/// so a client walking the whole list by page number would silently skip subjects.
/// </summary>
public sealed record Page<T>(IReadOnlyList<T> Items, Guid? NextCursor)
{
    public bool HasMore => NextCursor is not null;
}

/// <summary>One search result: the listing, plus how well and on which spelling it matched.</summary>
public sealed record SearchHit(DesignationView Designation, string MatchedName, double NameScore);
