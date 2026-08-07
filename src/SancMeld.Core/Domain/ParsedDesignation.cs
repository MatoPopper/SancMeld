namespace SancMeld.Core.Abstractions;

using SancMeld.Core.Domain;

/// <summary>
/// A connector's output: one listing by one source, already normalised into
/// the canonical shape but not yet versioned or persisted.
/// </summary>
public sealed record ParsedDesignation
{
    /// <summary>Publisher's own id where one exists (EU Entity_LogicalId, UN DATAID, FR IdRegistre).</summary>
    public string? ExternalId { get; init; }

    /// <summary>
    /// Never null. Where the publisher has no id, a documented deterministic
    /// fingerprint. Changing the recipe requires bumping FingerprintVersion.
    /// </summary>
    public required string SourceRecordKey { get; init; }
    public short FingerprintVersion { get; init; } = 1;

    public required SubjectType SubjectType { get; init; }
    public DesignationStatus Status { get; init; } = DesignationStatus.Active;

    public DateOnly? ListedAt { get; init; }
    public DateOnly? DelistedAt { get; init; }

    /// <summary>
    /// The role the publisher states — "Co-founder and co-owner of the Svetofor Group",
    /// "Oversaw Special Republican Guard".
    ///
    /// The closest thing to a reason that the machine-readable export carries. The
    /// narrative justification exists only in the Official Journal text, which is why
    /// every record links to the act.
    /// </summary>
    public string? Function { get; init; }

    /// <summary>Rank or honorific: "Col", "Dr", "Brigadier General".</summary>
    public string? Title { get; init; }

    public string? Reason { get; init; }
    public string? Remarks { get; init; }

    public IReadOnlyList<ParsedName> Names { get; init; } = [];
    public IReadOnlyList<ParsedBirth> Births { get; init; } = [];
    public IReadOnlyList<ParsedIdentifier> Identifiers { get; init; } = [];
    public IReadOnlyList<ParsedAddress> Addresses { get; init; } = [];
    public IReadOnlyList<ParsedNationality> Nationalities { get; init; } = [];
    public IReadOnlyList<MeasureType> Measures { get; init; } = [];
    public IReadOnlyList<string> Programmes { get; init; } = [];
    public IReadOnlyList<ParsedXref> CrossReferences { get; init; } = [];

    /// <summary>
    /// The acts this listing rests on. Without them a hit is an assertion; with them it
    /// carries the citation a compliance officer has to produce anyway.
    /// </summary>
    public IReadOnlyList<ParsedLegalInstrument> LegalBasis { get; init; } = [];

    /// <summary>
    /// Verbatim source fragment. Lets a mapping bug be fixed retroactively without
    /// re-downloading, and preserves fields not yet modelled.
    /// </summary>
    public required string RawRecordJson { get; init; }
}

public sealed record ParsedName
{
    public required string Value { get; init; }
    public NameType Kind { get; init; } = NameType.Alias;
    public string? GivenName { get; init; }
    public string? MiddleName { get; init; }
    public string? FamilyName { get; init; }
    public string? LanguageCode { get; init; }

    /// <summary>
    /// Generic alias that floods automated screening ("Abu Ahmad").
    /// UN individuals ship QUALITY = Low. UN *entities* reuse the same element
    /// for a.k.a./f.k.a. — different meaning, do not map it blindly.
    /// EU FSF has no quality field at all, so it must be derived.
    /// </summary>
    public bool IsWeak { get; init; }
    public WeakSource WeakSource { get; init; } = WeakSource.None;
}

public enum WeakSource { None, Source, Derived }

public sealed record ParsedBirth
{
    public short? Year { get; init; }
    public short? Month { get; init; }
    public short? Day { get; init; }
    public short? YearFrom { get; init; }
    public short? YearTo { get; init; }
    public DatePrecision Precision { get; init; } = DatePrecision.Unknown;
    public string? City { get; init; }
    public string? Region { get; init; }

    /// <summary>
    /// The locality as the source names it when it is not a city: a district, a village,
    /// a camp. Kept separate from City so a place name is never mistaken for one.
    /// </summary>
    public string? PlaceOriginal { get; init; }

    public string? CountryIso2 { get; init; }

    /// <summary>Keep the source wording: USSR, Yugoslavia, Kosovo, occupied territories.</summary>
    public string? CountryOriginal { get; init; }
}

public sealed record ParsedIdentifier
{
    public required IdentifierType Kind { get; init; }
    public required string ValueOriginal { get; init; }
    public required string ValueNormalized { get; init; }
    public string? IssuingCountry { get; init; }
    public DateOnly? IssuedAt { get; init; }
    public DateOnly? ExpiresAt { get; init; }
    public bool? IsDiplomatic { get; init; }

    /// <summary>
    /// The publisher says the document is a forgery. It stays a match surface — a fake
    /// passport is still the one the subject travels on — but it must not corroborate.
    /// </summary>
    public bool? IsKnownFalse { get; init; }

    public bool? IsExpired { get; init; }
    public string? Note { get; init; }
}

public sealed record ParsedAddress
{
    public string? Street { get; init; }
    public string? PoBox { get; init; }
    public string? City { get; init; }
    public string? PostalCode { get; init; }
    public string? Region { get; init; }
    public string? CountryIso2 { get; init; }
    public string? CountryOriginal { get; init; }

    /// <summary>Phone, fax, email and web, as the source published them.</summary>
    public string? ContactInfo { get; init; }

    /// <summary>
    /// Nothing more precise than a country. Such an address cannot corroborate a hit —
    /// "somewhere in Russia" is true of a great many people.
    /// </summary>
    public bool IsPartial { get; init; }
}

public sealed record ParsedNationality
{
    public string? CountryIso2 { get; init; }
    public required string CountryOriginal { get; init; }
}

/// <summary>
/// A legal act a listing cites — the regulation, the amendment that last touched it, or
/// the act implementing a UN designation.
///
/// The URL matters more than it looks: it is the difference between "our system says this
/// person is sanctioned" and "this person is sanctioned by Regulation 2022/1529, here is
/// the Official Journal text". Only the second survives a supervisor asking why.
/// </summary>
public sealed record ParsedLegalInstrument
{
    /// <summary>LISTED_BY | AMENDED_BY | DELISTED_BY | IMPLEMENTED_BY.</summary>
    public required string Relation { get; init; }

    /// <summary>The publisher's own wording: regulation, amendment, repealing, decision.</summary>
    public required string InstrumentType { get; init; }

    /// <summary>"2022/1529". Together with the type it identifies the act.</summary>
    public string? InstrumentNumber { get; init; }

    public string? Title { get; init; }
    public DateOnly? PublicationDate { get; init; }
    public DateOnly? EffectiveDate { get; init; }
    public string? OfficialUrl { get; init; }

    /// <summary>EUR-Lex identifier where the URL carries one, e.g. 32022R1529.</summary>
    public string? CelexNumber { get; init; }
}

/// <summary>
/// Publisher-declared link to a listing in another list, e.g. FR REFERENCE_UE.
/// These are free, authoritative "same entity" links and the strongest
/// deduplication signal available.
/// </summary>
public sealed record ParsedXref
{
    public required string TargetSourceCode { get; init; }
    public required string TargetExternalId { get; init; }
}
