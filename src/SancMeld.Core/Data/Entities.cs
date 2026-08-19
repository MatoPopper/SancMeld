using SancMeld.Core.Domain;

namespace SancMeld.Core.Data;

// ---------------------------------------------------------------------
// Source catalogue
// ---------------------------------------------------------------------

/// <summary>An issuing authority's territory. Member states with no national list get a row too.</summary>
public class Jurisdiction
{
    public Guid Id { get; set; }
    public required string Code { get; set; }          // 'EU', 'UN', 'FR'
    public required string Name { get; set; }
    public string? Iso2 { get; set; }
    public required string Kind { get; set; }          // EU | MEMBER_STATE | UN | THIRD_COUNTRY

    /// <summary>Why this jurisdiction has, or has not, a connector. Absence must be a recorded finding.</summary>
    public string ResearchStatus { get; set; } = "NEEDS_REVALIDATION";
    public string? ResearchEvidenceUrl { get; set; }
    public string? ResearchNote { get; set; }
    public DateTimeOffset? LastRevalidatedAt { get; set; }

    public List<SanctionsSource> Sources { get; set; } = [];
}

public class SanctionsSource
{
    public Guid Id { get; set; }

    /// <summary>Stable textual code, never a magic number. 'EU_FSF', 'UN_SC', 'OFAC_SDN'.</summary>
    public required string Code { get; set; }
    public required string Name { get; set; }
    public required string Publisher { get; set; }
    public Guid JurisdictionId { get; set; }
    public Jurisdiction? Jurisdiction { get; set; }

    /// <summary>Binding inside the EU, or advisory only (OFAC, UK OFSI) — surfaced as a red flag.</summary>
    public LegalEffect LegalEffect { get; set; }

    public required string Format { get; set; }        // XML | CSV | JSON | XLSX | ODS | HTML | PDF
    public required string LandingPageUrl { get; set; }
    public string? DownloadUrlTemplate { get; set; }

    /// <summary>OFAC's Sanctions List Service returns 403 without a User-Agent header.</summary>
    public bool RequiresUserAgent { get; set; } = true;

    public bool IsActive { get; set; } = true;
    public string? ParserKey { get; set; }
    public string? CronExpression { get; set; }

    /// <summary>Refuse to publish an import below this count; guards against a truncated download.</summary>
    public int? MinExpectedRecords { get; set; }

    public string? Notes { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

// ---------------------------------------------------------------------
// Ingest and audit trail
// ---------------------------------------------------------------------

public class ImportRun
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public SanctionsSource? Source { get; set; }

    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public required string Status { get; set; }        // RUNNING | SUCCESS | FAILED | SKIPPED_UNCHANGED | REJECTED_VALIDATION

    public int? HttpStatus { get; set; }
    public string? ETag { get; set; }
    public string? LastModified { get; set; }

    /// <summary>Identical to the previous successful run means the import is skipped without reparsing.</summary>
    public string? DownloadSha256 { get; set; }
    public long? DownloadBytes { get; set; }

    public string? ParserVersion { get; set; }
    public int? RecordsSeen { get; set; }
    public int? RecordsInserted { get; set; }
    public int? RecordsUpdated { get; set; }
    public int? RecordsUnchanged { get; set; }
    public int? RecordsDelisted { get; set; }
    public int WarningsCount { get; set; }
    public int ErrorsCount { get; set; }
    public string? ErrorSummary { get; set; }
}

/// <summary>Immutable record of exactly what was downloaded.</summary>
/// <summary>
/// One parsed record, held between Parse and Commit.
///
/// Staging exists so an import never has to choose between holding the whole dataset in
/// memory and comparing it against the live list record by record. It is written by the
/// streaming parser, the diff is a single join against it, and it is emptied once the
/// run has committed — nothing here is ever visible to a screening query.
/// </summary>
public class ImportStagingRecord
{
    public Guid ImportRunId { get; set; }
    public ImportRun? ImportRun { get; set; }

    public required string SourceRecordKey { get; set; }

    /// <summary>Hash of the mapped content, so the diff never compares field by field.</summary>
    public required string ContentSha256 { get; set; }

    /// <summary>The serialised ParsedDesignation, so Commit does not have to parse again.</summary>
    public required string Payload { get; set; }
}

public class SourceSnapshot
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public SanctionsSource? Source { get; set; }
    public Guid ImportRunId { get; set; }
    public ImportRun? ImportRun { get; set; }

    public DateTimeOffset RetrievedAt { get; set; }

    /// <summary>Publisher's own version marker: EU globalFileId, UN dateGenerated, OFAC publication date.</summary>
    public string? ExternalVersion { get; set; }
    public DateOnly? EffectiveDate { get; set; }

    public required string Sha256 { get; set; }
    public long ByteSize { get; set; }
    public string? ContentType { get; set; }
    public string? OriginalFilename { get; set; }

    /// <summary>Object storage or disk. Large artefacts do not belong in the database.</summary>
    public required string StorageUri { get; set; }
    public string? HttpHeadersJson { get; set; }
}

// ---------------------------------------------------------------------
// Sanctions data — versioned, never deleted
// ---------------------------------------------------------------------

/// <summary>
/// One listing by one source, in one revision.
///
/// Versioning is SCD Type 2: a content change closes the current row
/// (ValidTo = now) and inserts a new one. Current rows have ValidTo == null.
/// </summary>
public class Designation
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public SanctionsSource? Source { get; set; }

    /// <summary>Publisher's own id where one exists: EU Entity_LogicalId, UN DATAID, FR IdRegistre.</summary>
    public string? ExternalId { get; set; }

    /// <summary>
    /// The reference this publisher prints for the record, when it is not the id above.
    ///
    /// Publishers cite each other by these, not by internal ids: France's REFERENCE_UE
    /// says "EU.5878.67", which is the EU's euReferenceNumber and not its logicalId, so a
    /// declared link between two lists can only be resolved through this column. The UN
    /// needs none — its reference number IS its external id ("IRi.024").
    /// </summary>
    public string? PublisherReference { get; set; }

    /// <summary>Never null. Where the publisher has no id, a deterministic fingerprint.</summary>
    public required string SourceRecordKey { get; set; }
    public short FingerprintVersion { get; set; } = 1;

    public SubjectType SubjectType { get; set; }
    public DesignationStatus Status { get; set; } = DesignationStatus.Active;

    public DateOnly? ListedAt { get; set; }
    public DateOnly? DelistedAt { get; set; }
    public DateTimeOffset? UpdatedAtSource { get; set; }

    /// <summary>Stated role or position — the export's closest field to "why listed".</summary>
    public string? Function { get; set; }

    /// <summary>Rank or honorific.</summary>
    public string? Title { get; set; }

    public string? Reason { get; set; }
    public string? Remarks { get; set; }

    public DateTimeOffset ValidFrom { get; set; }
    public DateTimeOffset? ValidTo { get; set; }

    public Guid FirstSeenRunId { get; set; }
    public Guid LastSeenRunId { get; set; }

    /// <summary>
    /// The exact published file this revision was read from. A real foreign key, because a
    /// designation pointing at a snapshot that does not exist is a broken audit trail, and
    /// the audit trail is the point.
    /// </summary>
    public Guid SnapshotId { get; set; }
    public SourceSnapshot? Snapshot { get; set; }

    /// <summary>Verbatim source fragment, so a mapping bug can be fixed without re-downloading.</summary>
    public required string RawRecord { get; set; }
    public required string ContentSha256 { get; set; }

    public List<DesignationName> Names { get; set; } = [];
    public List<DesignationBirth> Births { get; set; } = [];
    public List<DesignationIdentifier> Identifiers { get; set; } = [];
    public List<DesignationAddress> Addresses { get; set; } = [];
    public List<DesignationNationality> Nationalities { get; set; } = [];
    public List<DesignationMeasure> Measures { get; set; } = [];
    public List<DesignationXref> CrossReferences { get; set; } = [];
    public List<DesignationProgramme> Programmes { get; set; } = [];
    public List<DesignationLegalInstrument> LegalInstruments { get; set; } = [];
}

/// <summary>The search hot path.</summary>
public class DesignationName
{
    public long Id { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public NameType Kind { get; set; }
    public required string NameOriginal { get; set; }

    /// <summary>
    /// Generated by the database from NameOriginal via sm_normalize_name(),
    /// so it can never drift from the source value. Carries the GIN trigram index.
    /// </summary>
    public string? NameNormalized { get; private set; }

    /// <summary>Double Metaphone, not Soundex: Soundex encodes English phonetics.</summary>
    public string? NameDmetaphone { get; set; }

    public string? GivenName { get; set; }
    public string? MiddleName { get; set; }
    public string? FamilyName { get; set; }
    public string? LanguageCode { get; set; }
    public string? ScriptCode { get; set; }

    /// <summary>UN individuals ship QUALITY = Low. EU has no quality field, so it is derived.</summary>
    public bool IsWeak { get; set; }
    public string WeakSource { get; set; } = "NONE";   // NONE | SOURCE | DERIVED
    public short? WeakRuleVersion { get; set; }
}

/// <summary>Decomposed on purpose: sources routinely state only a year, or a range.</summary>
public class DesignationBirth
{
    public long Id { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public short? Year { get; set; }
    public short? Month { get; set; }
    public short? Day { get; set; }
    public short? YearFrom { get; set; }
    public short? YearTo { get; set; }
    public DatePrecision Precision { get; set; } = DatePrecision.Unknown;

    public string? PlaceOriginal { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? CountryIso2 { get; set; }

    /// <summary>Keep the source wording: USSR, Yugoslavia, Kosovo, occupied territories.</summary>
    public string? CountryOriginal { get; set; }
    public string? Note { get; set; }
}

/// <summary>Deterministic match surface, checked before any fuzzy scoring.</summary>
public class DesignationIdentifier
{
    public long Id { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public IdentifierType Kind { get; set; }
    public required string ValueOriginal { get; set; }

    /// <summary>Normalisation is type-specific. A blanket punctuation strip corrupts national IDs.</summary>
    public required string ValueNormalized { get; set; }

    public string? IssuingCountry { get; set; }
    public DateOnly? IssuedAt { get; set; }
    public DateOnly? ExpiresAt { get; set; }
    public bool? IsDiplomatic { get; set; }
    public bool? IsKnownFalse { get; set; }
    public bool? IsExpired { get; set; }
    public string? Note { get; set; }
}

/// <summary>Structured, so filtering by country or city is possible at all.</summary>
public class DesignationAddress
{
    public long Id { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public string? AddressOriginal { get; set; }
    public string? Street { get; set; }
    public string? PoBox { get; set; }
    public string? PostalCode { get; set; }
    public string? City { get; set; }
    public string? Region { get; set; }
    public string? CountryIso2 { get; set; }
    public string? CountryOriginal { get; set; }
    public string? ContactInfo { get; set; }
    public bool IsPartial { get; set; }
}

public class DesignationNationality
{
    public long Id { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public string? CountryIso2 { get; set; }
    public required string CountryOriginal { get; set; }
    public string? Note { get; set; }
}

/// <summary>Screening must never infer an asset freeze from an entry-ban-only listing.</summary>
public class DesignationMeasure
{
    public long Id { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public MeasureType Kind { get; set; }
    public DateOnly? EffectiveFrom { get; set; }
    public DateOnly? EffectiveTo { get; set; }
    public string? ScopeText { get; set; }
}

/// <summary>
/// Publisher-declared link to a listing in another list (FR REFERENCE_UE, EU UnitedNationId).
/// The strongest deduplication signal available, and it costs nothing.
/// </summary>
public class DesignationXref
{
    public long Id { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public required string Kind { get; set; }          // UN_REFERENCE | EU_REFERENCE | OFAC_REFERENCE | OTHER
    public string? TargetSourceCode { get; set; }
    public required string TargetExternalId { get; set; }
}

public class SanctionProgramme
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public SanctionsSource? Source { get; set; }
    public required string Code { get; set; }
    public string? Name { get; set; }
}

public class LegalInstrument
{
    public Guid Id { get; set; }
    public Guid JurisdictionId { get; set; }
    public Jurisdiction? Jurisdiction { get; set; }

    public required string InstrumentType { get; set; }
    public string? InstrumentNumber { get; set; }
    public string? Title { get; set; }
    public DateOnly? PublicationDate { get; set; }
    public DateOnly? EffectiveDate { get; set; }
    public string? OfficialUrl { get; set; }
    public string? CelexNumber { get; set; }
}

/// <summary>
/// Which programmes a designation was listed under. Routinely more than one — an EU
/// entry can cite a country regime and a thematic regime at the same time.
/// </summary>
public class DesignationProgramme
{
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public Guid ProgrammeId { get; set; }
    public SanctionProgramme? Programme { get; set; }
}

/// <summary>
/// The legal acts behind a designation: which regulation listed the subject, which
/// amended the entry, which delisted it. This is what makes a past decision
/// defensible in front of a supervisor — the versioning alone only says a record
/// changed, not on what authority.
/// </summary>
public class DesignationLegalInstrument
{
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public Guid LegalInstrumentId { get; set; }
    public LegalInstrument? LegalInstrument { get; set; }

    /// <summary>
    /// LISTED_BY | AMENDED_BY | DELISTED_BY | IMPLEMENTED_BY. Part of the primary key:
    /// one act can amend several entries and delist others, so the pair alone is not unique.
    /// </summary>
    public required string Relation { get; set; }
}

// ---------------------------------------------------------------------
// Canonical layer — linking is separate and reversible
// ---------------------------------------------------------------------

public class CanonicalSubject
{
    public Guid Id { get; set; }
    public SubjectType SubjectType { get; set; }
    public required string DisplayName { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
}

/// <summary>Designations are never merged or rewritten; only the link is.</summary>
public class SubjectDesignationLink
{
    public Guid CanonicalSubjectId { get; set; }
    public CanonicalSubject? CanonicalSubject { get; set; }
    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }

    public decimal Confidence { get; set; }
    public required string Method { get; set; }        // MANUAL | DETERMINISTIC | XREF | PROBABILISTIC
    public string ReviewStatus { get; set; } = "PENDING_REVIEW";
    public string? Evidence { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

// ---------------------------------------------------------------------
// Screening and feedback
//
// Deliberately tenant-unaware. Multi-tenancy is added by the hosted edition
// as an additive migration: a tenant_id column defaulted from the session
// setting plus RLS policies. Core INSERTs never mention tenant_id.
// ---------------------------------------------------------------------

/// <summary>Versioned scoring configuration. Every stored score references one of these.</summary>
public class MatchAlgorithm
{
    public Guid Id { get; set; }
    public required string Code { get; set; }          // 'RULES', 'ML_LIGHTGBM'
    public required string Version { get; set; }

    /// <summary>Version of sm_normalize_name(); changing it invalidates stored normalised names.</summary>
    public short NameNormVersion { get; set; }
    public short FeatureSetVersion { get; set; }

    public string? ModelUri { get; set; }
    public string? ModelSha256 { get; set; }
    public string? Parameters { get; set; }
    public bool IsActive { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

public class ScreeningRequest
{
    public Guid Id { get; set; }

    public required string QueryOriginal { get; set; }
    public required string QueryNormalized { get; set; }
    public SubjectType? QuerySubjectType { get; set; }
    public short? QueryBirthYear { get; set; }
    public string? QueryCountryIso2 { get; set; }
    public string? QueryIdentifiers { get; set; }

    public Guid AlgorithmId { get; set; }
    public MatchAlgorithm? Algorithm { get; set; }
    public decimal ThresholdUsed { get; set; }

    public DateTimeOffset RequestedAt { get; set; }
    public string? RequestedBy { get; set; }
    public int? DurationMs { get; set; }
    public int CandidateCount { get; set; }
    public string? Channel { get; set; }               // API | UI | BATCH

    public List<ScreeningCandidate> Candidates { get; set; } = [];
}

/// <summary>
/// One row per candidate SHOWN to the user — the training set.
///
/// Features are materialised, not recomputed later: lists change and scoring
/// gets retuned, so a recomputation would yield different numbers.
/// </summary>
public class ScreeningCandidate
{
    public long Id { get; set; }
    public Guid RequestId { get; set; }
    public ScreeningRequest? Request { get; set; }

    public Guid DesignationId { get; set; }
    public Designation? Designation { get; set; }
    public long? MatchedNameId { get; set; }

    /// <summary>Which list version was in play; without it a past decision cannot be reconstructed.</summary>
    public Guid SnapshotId { get; set; }

    public int Rank { get; set; }
    public decimal Score { get; set; }
    public decimal? NameScore { get; set; }
    public bool AboveThreshold { get; set; }

    /// <summary>
    /// Deliberately pulled from BELOW the threshold to counter the feedback-loop
    /// blind spot: without these the model only learns about what it already shows.
    /// </summary>
    public bool IsExploration { get; set; }

    public required string Features { get; set; }
    public Guid AlgorithmId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>Coded rejection reasons. Free text cannot be learned from.</summary>
public class AdjudicationReason
{
    public required string Code { get; set; }
    public required string Label { get; set; }
    public bool IsNegative { get; set; }
}

public class Adjudication
{
    public long Id { get; set; }
    public long CandidateId { get; set; }
    public ScreeningCandidate? Candidate { get; set; }

    /// <summary>Four-valued: collapsing uncertainty into "no" destroys the training signal.</summary>
    public AdjudicationDecision Decision { get; set; }
    public string? ReasonCode { get; set; }
    public string? Note { get; set; }

    public required string DecidedBy { get; set; }
    public DateTimeOffset DecidedAt { get; set; }
    public int? DurationMs { get; set; }

    /// <summary>Corrections are new rows, not updates.</summary>
    public long? SupersedesId { get; set; }
}

/// <summary>
/// Training corpus. Deliberately carries no name, no timestamp finer than a
/// month and no designation id — only derived comparison features, from which
/// the original names cannot be reconstructed.
/// </summary>
public class TrainingSample
{
    public long Id { get; set; }
    public required string Features { get; set; }
    public bool Label { get; set; }

    public string? SourceCode { get; set; }
    public LegalEffect? LegalEffect { get; set; }
    public SubjectType? SubjectType { get; set; }
    public bool WasExploration { get; set; }
    public string? ReasonCode { get; set; }

    public short FeatureSetVersion { get; set; }
    public short NameNormVersion { get; set; }
    public DateOnly PeriodMonth { get; set; }

    /// <summary>Public-list pairs involve no customer personal data and dominate the corpus.</summary>
    public required string Origin { get; set; }        // PUBLIC_LIST_ALIAS | PUBLIC_LIST_XREF | ADJUDICATION
}

// ---------------------------------------------------------------------
// Access control
//
// An allow list, and nothing more: this host is single-tenant, so there is
// nobody to be an administrator *of*. A row here means one Google account may
// call the endpoints that manage access. The hosted edition replaces this
// entirely with tenants and roles; it does not extend it.
// ---------------------------------------------------------------------

/// <summary>
/// A Google account allowed to administer this instance.
///
/// The identity provider proves who somebody is; this table decides whether that
/// person is anyone here. The two are deliberately separate — anybody in the world
/// can obtain a valid Google token, and none of them are administrators of this
/// deployment until an existing one says so.
/// </summary>
public class Admin
{
    public Guid Id { get; set; }

    /// <summary>
    /// Lower-cased on the way in, and the only thing matched against the token.
    /// Google delivers addresses in whatever case the user typed them.
    /// </summary>
    public required string Email { get; set; }

    public string? DisplayName { get; set; }

    /// <summary>
    /// Google's own subject id, recorded on first sign-in and never matched against.
    ///
    /// Matching on the address is what an operator can actually administer — nobody
    /// knows their own 'sub'. Keeping it anyway means a later address change can be
    /// recognised as the same person rather than guessed at.
    /// </summary>
    public string? GoogleSubject { get; set; }

    /// <summary>
    /// Access is revoked, not deleted, so the row keeps saying who granted it and when.
    /// Granting again clears the revocation; if a full history of who held access on a
    /// given day is ever needed, that is a separate append-only table, not more columns
    /// here. Every grant and revocation is logged in the meantime.
    /// </summary>
    public bool IsActive { get; set; } = true;

    public string? Note { get; set; }

    public DateTimeOffset CreatedAt { get; set; }

    /// <summary>Who granted it: another administrator's address, or 'cli:&lt;user&gt;'.</summary>
    public required string CreatedBy { get; set; }

    public DateTimeOffset? RevokedAt { get; set; }
    public string? RevokedBy { get; set; }

    /// <summary>
    /// Last accepted request, updated at most once a minute. This is how an allow list
    /// gets pruned: an account nobody has used for a year is one nobody needs.
    /// </summary>
    public DateTimeOffset? LastSeenAt { get; set; }
}
