using SancMeld.Core.Domain;

namespace SancMeld.Core.Learning;

/// <summary>
/// What an analyst decided about one check, in one round trip.
///
/// A review is one act — "I looked at these six and this is the one" — so it is recorded
/// as one act. Sending each decision separately would let a half-recorded review exist,
/// which is exactly the state nobody can reconstruct later.
/// </summary>
public sealed record AdjudicationRequest
{
    /// <summary>The <c>checkId</c> from /check, or <c>requestId</c> from /screen.</summary>
    public required Guid CheckId { get; init; }

    public required string DecidedBy { get; init; }

    /// <summary>How long the analyst spent, if the UI measures it. Useful, never required.</summary>
    public int? DurationMs { get; init; }

    public IReadOnlyList<AdjudicationDecisionInput> Decisions { get; init; } = [];
}

public sealed record AdjudicationDecisionInput
{
    /// <summary>
    /// Identifies the listing being decided on, as returned in the check.
    ///
    /// One listing, not one subject: a person on the EU and the UN lists is two listings,
    /// and each is a separate legal fact that an analyst may see differently. Deciding
    /// them together would record an opinion nobody actually gave.
    /// </summary>
    public required Guid DesignationId { get; init; }

    public required AdjudicationDecision Decision { get; init; }

    /// <summary>
    /// One of the codes from /adjudications/reasons. Optional, and worth insisting on in
    /// the UI for negatives: "why not" is the part that teaches, and the part a supervisor
    /// asks about.
    /// </summary>
    public string? ReasonCode { get; init; }

    public string? Note { get; init; }

    /// <summary>
    /// The id of a decision this one corrects. Corrections are new rows — an audit trail
    /// that can be edited is not one.
    /// </summary>
    public long? Supersedes { get; init; }
}

public sealed record AdjudicationResult
{
    public required Guid CheckId { get; init; }
    public required int Recorded { get; init; }

    /// <summary>
    /// How many of those became labelled training data. Fewer than <see cref="Recorded"/>
    /// whenever an analyst answered "undecided" or "needs more data" — honest uncertainty
    /// is worth recording and cannot be labelled.
    /// </summary>
    public required int Labelled { get; init; }

    public IReadOnlyList<string> Warnings { get; init; } = [];
}

public sealed record AdjudicationReasonView(string Code, string Label, bool IsNegative);

/// <summary>
/// One labelled comparison, for training a model outside this system.
///
/// Deliberately carries no personal data: no name, no query, no date of birth — only the
/// feature vector, the label and enough metadata to slice the corpus. A model can be
/// trained from these without the training set ever becoming a copy of somebody's
/// customer file.
/// </summary>
public sealed record TrainingSampleView(
    long Id,
    string Features,
    bool Label,
    string Origin,
    string? SourceCode,
    LegalEffect? LegalEffect,
    SubjectType? SubjectType,
    bool WasExploration,
    string? ReasonCode,
    short FeatureSetVersion,
    short NameNormVersion,
    DateOnly PeriodMonth);
