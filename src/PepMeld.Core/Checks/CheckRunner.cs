using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;

namespace PepMeld.Core.Checks;

/// <summary>
/// Runs one check through its stages, writing each stage's findings as soon as it ends.
/// The caller polling GET /checks/{id} sees the lists within seconds while a later stage
/// is still running, and a failure in a later stage keeps what the earlier ones found.
/// </summary>
public sealed class CheckRunner(
    PepMeldDbContext db,
    IEnumerable<ICheckStage> stages,
    IOptions<PepMeldOptions> options,
    TimeProvider clock,
    ILogger<CheckRunner> logger)
{
    public async Task RunAsync(Guid checkId, CancellationToken ct)
    {
        var check = await db.Checks
            .Include(c => c.Stages)
            .FirstOrDefaultAsync(c => c.Id == checkId, ct)
            .ConfigureAwait(false);

        if (check is null || check.Status != CheckStatus.Running)
        {
            logger.LogWarning("Check {CheckId} is not waiting to run; ignoring.", checkId);
            return;
        }

        var findings = new List<FindingView>();

        foreach (var stageRun in check.Stages.OrderBy(s => s.Stage))
        {
            if (stageRun.Status != StageStatus.Pending)
            {
                continue;
            }

            var stage = stages.FirstOrDefault(s => s.Stage == stageRun.Stage);
            if (stage is null)
            {
                stageRun.Status = StageStatus.Skipped;
                stageRun.Note = stageRun.Stage == CheckStage.Boards
                    ? "Orgány štátnych podnikov a firiem so štátnou účasťou sú súčasťou etapy Lists (zdroje CZ_ARES_STATE, SK_NRSR_VNF). Samostatná etapa pre spoločné pôsobenie v orgánoch (blízke osoby) nie je v tejto verzii k dispozícii."
                    : "Táto etapa nie je v tejto verzii k dispozícii.";
                stageRun.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                continue;
            }

            stageRun.Status = StageStatus.Running;
            stageRun.StartedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);

            try
            {
                var result = await stage.RunAsync(check, ct).ConfigureAwait(false);

                foreach (var finding in result.Findings)
                {
                    db.Findings.Add(new Finding
                    {
                        Id = Guid.CreateVersion7(),
                        CheckId = check.Id,
                        Stage = stageRun.Stage,
                        Kind = finding.Kind,
                        Level = finding.Level,
                        PayloadJson = JsonSerializer.Serialize(finding, PepMeldJson.Options),
                        CreatedAt = clock.GetUtcNow(),
                    });
                }

                findings.AddRange(result.Findings);
                stageRun.Status = StageStatus.Completed;
                stageRun.Note = result.Note;
                stageRun.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Stage {Stage} of check {CheckId} failed.", stageRun.Stage, check.Id);
                db.ChangeTracker.Clear();
                db.Attach(check);
                stageRun.Status = StageStatus.Failed;
                stageRun.Error = ex.Message;
                stageRun.FinishedAt = clock.GetUtcNow();
                check.Status = CheckStatus.Failed;
                check.Error = $"Etapa {stageRun.Stage} zlyhala: {ex.Message}";
                check.FinishedAt = clock.GetUtcNow();
                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                return;
            }
        }

        var coverage = check.CoverageJson is null
            ? []
            : JsonSerializer.Deserialize<List<CoverageView>>(check.CoverageJson, PepMeldJson.Options) ?? [];

        var (outcome, summary) = Outcomes.Derive(findings, coverage, options.Value.Matching.FormerPepMonths);
        check.Outcome = outcome;
        check.Summary = summary;
        check.Status = CheckStatus.Completed;
        check.FinishedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation("Check {CheckId}: {Outcome} with {Findings} finding(s).", check.Id, outcome, findings.Count);
    }
}
