namespace SancMeld.Core.Relations;

/// <summary>
/// A register connector, as the import sees it: given the listings an import just wrote,
/// look them up and read their relations, now rather than at the next scheduled run.
///
/// A new listing is the moment the question "what else does this touch" is asked, and
/// the answer should be there when the officer opens the listing the same morning. The
/// import does not know which registers exist; it hands the ids to whoever registered,
/// and a register that is down costs a warning, never the import.
/// </summary>
public interface IRelatedPartyEnricher
{
    Task EnrichAsync(IReadOnlyCollection<Guid> designationIds, CancellationToken ct = default);
}
