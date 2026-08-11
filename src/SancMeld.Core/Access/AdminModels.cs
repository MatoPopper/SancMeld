namespace SancMeld.Core.Access;

/// <summary>One entry of the allow list, as it is shown and returned.</summary>
public sealed record AdminView(
    Guid Id,
    string Email,
    string? DisplayName,
    bool IsActive,
    string? Note,
    DateTimeOffset CreatedAt,
    string CreatedBy,
    DateTimeOffset? RevokedAt,
    string? RevokedBy,
    DateTimeOffset? LastSeenAt);

/// <summary>
/// Why a revocation did not happen.
///
/// Spelled out rather than returned as a bare false: "there is no such entry" and
/// "this is the last person who can still get in" call for entirely different
/// answers, and the caller cannot tell them apart from a boolean.
/// </summary>
public enum RevokeOutcome
{
    Revoked,
    NotFound,
    AlreadyRevoked,

    /// <summary>
    /// Refused. Revoking the last active entry locks everybody out of the admin API,
    /// and the only way back in is a shell on the server.
    /// </summary>
    LastActiveAdmin,
}
