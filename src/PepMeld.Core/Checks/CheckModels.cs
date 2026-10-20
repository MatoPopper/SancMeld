using System.Text.Json;
using System.Text.Json.Serialization;
using PepMeld.Core.Domain;

namespace PepMeld.Core.Checks;

/// <summary>
/// How findings are serialised into the database and out of the API: one setting, so a
/// stored finding reads back exactly as it was returned. Enums as text, camelCase, nulls
/// left out.
/// </summary>
public static class PepMeldJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}

/// <summary>What a caller sends to POST /checks. The date of birth is mandatory: a PEP check without one is a guess among namesakes.</summary>
public sealed record NewCheck(
    string Name,
    DateOnly? DateOfBirth,
    Country? Country,
    string? Nationality,
    string? PlaceOfBirth,
    string? Reference,
    IReadOnlyList<CheckStage>? Scope);

public enum CreateOutcome
{
    Created,

    /// <summary>The caller already has a check running; the result carries it.</summary>
    AlreadyRunning,

    Invalid,
}

public sealed record CreateCheckResult(CreateOutcome Outcome, CheckView? Check, string? Message);

public sealed record CheckView(
    Guid CheckId,
    CheckStatus Status,
    CheckQueryView Query,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    IReadOnlyList<StageView> Stages,
    CheckOutcome? Outcome,
    string? Summary,
    string? Error,
    IReadOnlyList<CoverageView> Coverage);

public sealed record CheckQueryView(
    string Name,
    DateOnly DateOfBirth,
    Country? Country,
    string? Nationality,
    string? PlaceOfBirth,
    string? Reference,
    IReadOnlyList<CheckStage> Scope);

public sealed record StageView(
    CheckStage Stage,
    StageStatus Status,
    DateTimeOffset? StartedAt,
    DateTimeOffset? FinishedAt,
    string? Note,
    string? Error,
    IReadOnlyList<FindingView> Findings);

/// <summary>One thing found about the person. The shape is the same for every stage; fields that do not apply are null.</summary>
public sealed record FindingView(
    FindingKind Kind,
    EvidenceLevel Level,
    FindingPerson Person,
    FindingFunction? Function,
    FamilyRelation? Relation,
    string? RelatedTo,
    int? MonthsSinceEnd,
    IReadOnlyList<Evidence> Evidence,
    string Explanation,
    string? ToVerify);

public sealed record FindingPerson(
    string Name,
    DateOnly? DateOfBirth,
    bool DateOfBirthMatched,
    string? Residence);

public sealed record FindingFunction(
    Country Country,
    PepCategory Category,
    string LegalBasis,
    string AmlrCategory,
    string Institution,
    string Role,
    DateOnly? From,
    DateOnly? To,
    bool EndedByDisappearance,
    string? DateNote);

public sealed record Evidence(string Url, DateTimeOffset? RetrievedAt, string? Quote);

/// <summary>A source as it was at the moment of the check: what was searched and how fresh it was.</summary>
public sealed record CoverageView(
    string Source,
    Country Country,
    string Name,
    bool HasBirthDate,
    DateTimeOffset? LastImportedAt,
    int Persons);

/// <summary>A check in the caller's history.</summary>
public sealed record CheckSummaryView(
    Guid CheckId,
    CheckStatus Status,
    string Name,
    DateOnly DateOfBirth,
    Country? Country,
    string? Reference,
    DateTimeOffset StartedAt,
    DateTimeOffset? FinishedAt,
    CheckOutcome? Outcome,
    string? Summary);

/// <summary>What a stage hands back: its findings and a note for the reader.</summary>
public sealed record StageResult(IReadOnlyList<FindingView> Findings, string? Note);
