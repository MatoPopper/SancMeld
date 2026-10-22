using PepMeld.Core.Domain;

namespace PepMeld.Core.Data;

/// <summary>A list this instance imports: who publishes it, what it covers, whether it carries a date of birth.</summary>
public class PepSource
{
    public Guid Id { get; set; }
    public required string Code { get; set; }
    public Country Country { get; set; }
    public required string Name { get; set; }
    public required string Publisher { get; set; }
    public required string Url { get; set; }

    /// <summary>
    /// Whether the source publishes a date of birth. The single most important property
    /// of a source: with it a name match is confirmed or excluded, without it the match
    /// stays a candidate and the answer has to say so.
    /// </summary>
    public bool HasBirthDate { get; set; }

    /// <summary>
    /// Whether the publisher is the institution itself or a register the law keeps.
    /// A name and date of birth from an official source confirm a match (level A); the
    /// same from a source anyone can edit, such as Wikidata, is level B: strong, cited,
    /// and still worth a look at the institution's own page.
    /// </summary>
    public bool IsOfficial { get; set; } = true;

    public string? Licence { get; set; }

    /// <summary>A parse that yields fewer persons than this is refused rather than applied: a truncated file must not end everyone's functions.</summary>
    public int MinExpectedPersons { get; set; }

    /// <summary>How old the last successful import may be before the scheduler runs another.</summary>
    public int RefreshHours { get; set; } = 24;

    public bool IsEnabled { get; set; } = true;
    public DateTimeOffset CreatedAt { get; set; }
}

public class ImportRun
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public PepSource? Source { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ImportStatus Status { get; set; }

    /// <summary>Where the downloaded bytes are kept, so a past check can be replayed against what was known then.</summary>
    public string? SnapshotPath { get; set; }
    public string? ContentSha256 { get; set; }
    public string? SourceUrl { get; set; }

    public int PersonsSeen { get; set; }
    public int PersonsInserted { get; set; }
    public int PersonsUpdated { get; set; }
    public int FunctionsSeen { get; set; }
    public int FunctionsInserted { get; set; }
    public int FunctionsUpdated { get; set; }

    /// <summary>Open functions the file no longer carried, closed as of this run.</summary>
    public int FunctionsEnded { get; set; }

    public string? Error { get; set; }
}

/// <summary>A person as one source records them. The same human in two sources is two rows; nothing merges them.</summary>
public class PepPerson
{
    public Guid Id { get; set; }
    public Guid SourceId { get; set; }
    public PepSource? Source { get; set; }

    /// <summary>The publisher's own identifier, or a documented fingerprint where there is none.</summary>
    public required string SourceKey { get; set; }

    /// <summary>Given name then family name, without titles, as shown to the caller.</summary>
    public required string DisplayName { get; set; }

    /// <summary>
    /// <see cref="DisplayName"/> through SancMeld's NameNormalizer, written at import.
    /// The trigram index is built on it and a query is normalised the same way before it
    /// is compared, which is what keeps the two in step.
    /// </summary>
    public required string NameNormalized { get; set; }

    public string? GivenName { get; set; }
    public string? FamilyName { get; set; }
    public string? TitleBefore { get; set; }
    public string? TitleAfter { get; set; }

    /// <summary>
    /// Null when the source does not publish it or marks it unknown, unless another list
    /// supplied it: then <see cref="BirthDateFrom"/> says which.
    /// </summary>
    public DateOnly? BirthDate { get; set; }

    /// <summary>
    /// The code of the list <see cref="BirthDate"/> was taken from, when this source does not
    /// publish it; null when the date is the source's own. psp.cz marks the date of birth of
    /// most senators as unknown, Wikidata has it for the same person in the same office, and
    /// a date the client must give is worth little against a record that has none. A
    /// borrowed date is never better than its lender: a match on it is level B at most.
    /// </summary>
    public string? BirthDateFrom { get; set; }

    /// <summary>The lender's page for <see cref="BirthDate"/>, cited next to the source's own.</summary>
    public string? BirthDateFromUrl { get; set; }
    public DateOnly? DeathDate { get; set; }
    public string? BirthPlace { get; set; }
    public string? Residence { get; set; }

    /// <summary>The page a person can be checked on, cited as evidence.</summary>
    public required string SourceUrl { get; set; }

    public Guid FirstSeenRunId { get; set; }
    public Guid LastSeenRunId { get; set; }

    public ICollection<PepFunction> Functions { get; set; } = [];
}

/// <summary>
/// One function of one person, with its dates. Nothing is deleted: a function that ends
/// gets <see cref="ValidTo"/> and stays, because the person stays politically exposed
/// for at least twelve months after it (SK § 12 ods. 3) and "is or was" in Czech law.
/// </summary>
public class PepFunction
{
    public Guid Id { get; set; }
    public Guid PersonId { get; set; }
    public PepPerson? Person { get; set; }

    /// <summary>The publisher's identifier of this membership or office, unique within the person.</summary>
    public required string SourceKey { get; set; }

    public Country Country { get; set; }
    public PepCategory Category { get; set; }

    /// <summary>The paragraph as cited at import, see <see cref="Domain.LegalBasis"/>.</summary>
    public required string LegalBasis { get; set; }

    public required string Institution { get; set; }
    public required string Role { get; set; }
    public DateOnly? ValidFrom { get; set; }
    public DateOnly? ValidTo { get; set; }

    /// <summary>
    /// True when the end date is ours, not the publisher's: the function was in the file
    /// on one import and gone on the next. Such a date is only as precise as the import
    /// schedule, and the answer says so.
    /// </summary>
    public bool EndedByDisappearance { get; set; }

    /// <summary>What the dates are when they are a term's bounds rather than the person's own mandate; null when they are the person's.</summary>
    public string? DateNote { get; set; }

    public string? SourceUrl { get; set; }
    public Guid FirstSeenRunId { get; set; }
    public Guid LastSeenRunId { get; set; }
}

/// <summary>Everyone who may run a check, with a request budget. The same shape as SancMeld's api_user, in this database, until the two share an account.</summary>
public class ApiUser
{
    public Guid Id { get; set; }

    /// <summary>Lower-cased; matched against the verified address in the token or the key's owner.</summary>
    public required string Email { get; set; }
    public string? DisplayName { get; set; }
    public string? GoogleSubject { get; set; }

    /// <summary>Checks this address may start in total. Raised by hand in the database or with the CLI.</summary>
    public int RequestLimit { get; set; }
    public int RequestsUsed { get; set; }

    public bool IsBlocked { get; set; }
    public string? BlockedBy { get; set; }
    public DateTimeOffset? BlockedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastSeenAt { get; set; }
}

public class ApiKey
{
    public Guid Id { get; set; }
    public required string Name { get; set; }
    public required string OwnerEmail { get; set; }

    /// <summary>Lowercase hex SHA-256 of the key.</summary>
    public required string KeyHash { get; set; }
    public required string DisplayPrefix { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastUsedAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
}

/// <summary>
/// A check: who asked, what they asked, and how it went. The query is kept verbatim
/// because the answer is only meaningful next to the question it answered.
/// </summary>
public class PepCheck
{
    public Guid Id { get; set; }
    public required string UserEmail { get; set; }

    public required string Name { get; set; }
    public DateOnly BirthDate { get; set; }
    public Country? Country { get; set; }
    public string? Nationality { get; set; }
    public string? PlaceOfBirth { get; set; }

    /// <summary>The caller's own reference, e.g. a KYC case number, returned with the result and kept for the audit.</summary>
    public string? Reference { get; set; }

    /// <summary>Stage names, as text so the database needs no enum type.</summary>
    public string[] Scope { get; set; } = [];

    public CheckStatus Status { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public CheckOutcome? Outcome { get; set; }

    /// <summary>One sentence for a person, in Slovak.</summary>
    public string? Summary { get; set; }
    public string? Error { get; set; }

    /// <summary>What was searched: the sources and their last import at the moment of the check, as JSON. "What we knew on the day."</summary>
    public string? CoverageJson { get; set; }

    public ICollection<PepCheckStage> Stages { get; set; } = [];
    public ICollection<Finding> Findings { get; set; } = [];
}

public class PepCheckStage
{
    public Guid CheckId { get; set; }
    public CheckStage Stage { get; set; }
    public StageStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }

    /// <summary>What the stage wants the reader to know: what it could not cover, why it was skipped.</summary>
    public string? Note { get; set; }
    public string? Error { get; set; }
}

/// <summary>A finding, stored as the JSON the API returns, so a past answer is reproduced exactly and not re-rendered from today's data.</summary>
public class Finding
{
    public Guid Id { get; set; }
    public Guid CheckId { get; set; }
    public CheckStage Stage { get; set; }
    public FindingKind Kind { get; set; }
    public EvidenceLevel Level { get; set; }
    public required string PayloadJson { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
}

/// <summary>
/// One research run for the family of one PEP: who did it, when, how long it took and what
/// it cost in searches. Kept so the time per PEP is measured rather than guessed, and so a
/// tie can be traced to the run that found it.
/// </summary>
public class FamilyResearchRun
{
    public Guid Id { get; set; }
    public required string PepName { get; set; }
    public DateOnly PepBirthDate { get; set; }
    public Country PepCountry { get; set; }

    /// <summary>"claude-code-agent" and the like: what ran the research.</summary>
    public required string Agent { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset FinishedAt { get; set; }
    public int Searches { get; set; }
    public int PagesOpened { get; set; }
    public int RelativesFound { get; set; }

    /// <summary>Relatives the file named that were refused at import, and why: no evidence, no quote.</summary>
    public int RelativesRefused { get; set; }

    /// <summary>Ties this run found that no earlier run had.</summary>
    public int RelativesNew { get; set; }

    /// <summary>
    /// Ties an earlier run found and this one did not name: a divorce, a death, or a search
    /// that missed. Kept, not deleted; their LastFoundAt says since when nobody has seen them.
    /// </summary>
    public int RelativesNoLongerNamed { get; set; }
    public string? Notes { get; set; }

    /// <summary>The file as delivered, verbatim, for the audit.</summary>
    public required string RawJson { get; set; }
    public DateTimeOffset ImportedAt { get; set; }

    public double DurationSeconds => (FinishedAt - StartedAt).TotalSeconds;
}

/// <summary>
/// A family tie of a PEP: spouse or partner, child, child's spouse, parent, sibling. The
/// PEP is named by name and date of birth, not by a person row, because the same human is a
/// row in every list that names them. A relative is a private person: only the name, the
/// tie, a published date or year of birth and the evidence are kept. A minor is not kept at
/// all: a child under 18 cannot be a client or a counterparty on their own, so the tie adds
/// nothing to a check and would only hold a child's name.
/// </summary>
public class FamilyLink
{
    public Guid Id { get; set; }

    /// <summary>The last research run that named the tie; null for a tie only Wikidata has.</summary>
    public Guid? RunId { get; set; }

    public required string PepName { get; set; }
    public required string PepNameNormalized { get; set; }
    public DateOnly PepBirthDate { get; set; }
    public Country PepCountry { get; set; }

    public FamilyRelation Relation { get; set; }
    public required string RelativeName { get; set; }
    public required string RelativeNameNormalized { get; set; }
    public DateOnly? RelativeBirthDate { get; set; }
    public int? RelativeBirthYear { get; set; }

    /// <summary>current, former, deceased or unknown. A former spouse is no family member in law.</summary>
    public required string Status { get; set; }
    public required string Confidence { get; set; }

    /// <summary>[{url, quote, publisher, retrievedAt}] as JSON; every tie has at least one.</summary>
    public required string EvidenceJson { get; set; }
    public string? Note { get; set; }

    public FamilyFoundBy FoundBy { get; set; }
    public EvidenceLevel Level { get; set; }
    public FamilyReviewStatus ReviewStatus { get; set; }
    public string? DecidedBy { get; set; }
    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }

    /// <summary>When a research run last named this tie: the date the answer cites, and what monitoring watches.</summary>
    public DateTimeOffset LastFoundAt { get; set; }
}

/// <summary>One pass over a structured source of family ties (Wikidata): when, and what it found.</summary>
public class FamilySync
{
    public Guid Id { get; set; }
    public required string Source { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public ImportStatus Status { get; set; }
    public int Peps { get; set; }
    public int TiesSeen { get; set; }
    public int Inserted { get; set; }
    public int Updated { get; set; }
    public string? Error { get; set; }
}
