using SancMeld.Core.Domain;

namespace SancMeld.Core.Search;

public sealed record ScreeningQuery
{
    public required string Name { get; init; }

    /// <summary>Person or organisation, when the caller knows. Absence is not a mismatch.</summary>
    public SubjectType? SubjectType { get; init; }

    public short? BirthYear { get; init; }
    public string? CountryIso2 { get; init; }
    public IReadOnlyList<string> Identifiers { get; init; } = [];

    public string? RequestedBy { get; init; }
    public string? Channel { get; init; }
}

/// <summary>
/// What a qualifier said — with "the list does not know" kept separate from "the list
/// disagrees".
///
/// This distinction is the whole difficulty of screening sanctions data. Most listings are
/// incomplete: on the EU list, 38 % of subjects have no date of birth at all. Folding
/// <see cref="UnknownOnRecord"/> into <see cref="Contradicts"/> would push exactly those
/// subjects below the threshold — the ones nobody knows enough about — and a missed match
/// is a legal breach. Folding it into <see cref="Agrees"/> would flood the analyst.
/// It has to stay its own answer, and it has to reach the caller.
/// </summary>
public enum QualifierState
{
    /// <summary>The caller did not supply it, so nothing can be concluded.</summary>
    NotProvided,

    /// <summary>The listing does not carry it. Not evidence either way.</summary>
    UnknownOnRecord,

    Agrees,
    Contradicts,
}

/// <summary>
/// Why a candidate scored what it did. Served with every hit so a decision can be
/// defended later, and stored so it can become training data.
/// </summary>
public sealed record MatchExplanation
{
    public required double NameScore { get; init; }
    public required bool IdentifierMatch { get; init; }
    public required QualifierState BirthYear { get; init; }
    public required QualifierState Country { get; init; }
    public required QualifierState SubjectType { get; init; }
    public IReadOnlyList<string> Notes { get; init; } = [];
}

public sealed record ScreeningHit
{
    public required DesignationView Designation { get; init; }
    public required string MatchedName { get; init; }
    public required double Score { get; init; }
    public required bool AboveThreshold { get; init; }
    public required MatchExplanation Explanation { get; init; }
}

public sealed record ScreeningResult
{
    /// <summary>Identifies the stored request, and is what an adjudication is attached to.</summary>
    public required Guid RequestId { get; init; }

    public required string Query { get; init; }
    public required double Threshold { get; init; }
    public required int CandidatesConsidered { get; init; }

    /// <summary>Ordered by score. Includes candidates below the threshold, flagged as such.</summary>
    public IReadOnlyList<ScreeningHit> Hits { get; init; } = [];
}
