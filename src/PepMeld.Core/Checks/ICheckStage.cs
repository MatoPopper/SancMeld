using PepMeld.Core.Data;
using PepMeld.Core.Domain;

namespace PepMeld.Core.Checks;

/// <summary>
/// One stage of a check. Stages are registered in the container; a stage of the scope
/// that has no implementation is reported as skipped with a note, not as an error, so
/// the API shape is complete before every stage exists.
///
/// A stage must be idempotent and must not assume an earlier stage succeeded: the
/// runner writes each stage's findings as soon as it finishes, and a failure in the
/// third stage keeps the first two.
/// </summary>
public interface ICheckStage
{
    CheckStage Stage { get; }

    Task<StageResult> RunAsync(PepCheck check, CancellationToken ct);
}
