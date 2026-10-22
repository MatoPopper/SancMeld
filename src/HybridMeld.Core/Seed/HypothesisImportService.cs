using System.Text.Json;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SancMeld.Core.Matching;

namespace HybridMeld.Core.Seed;

/// <summary>
/// A hypothesis from a file, the way a case comes from one. Two differences on purpose:
/// related actors are matched to existing actors by exact name or alias and never
/// created (a hypothesis is not where an actor is introduced), and indicators are matched
/// by statement, so a re-import adds what is new and changes nothing else.
/// </summary>
public sealed class HypothesisImportService(HybridMeldDbContext db, HypothesisService hypotheses, ILogger<HypothesisImportService> logger)
{
    public sealed record HypothesisFile(FileHypothesis Hypothesis, IReadOnlyList<string>? Actors, IReadOnlyList<FileIndicator>? Indicators);

    public sealed record FileHypothesis(
        string Slug, string Title, string Question, string Rationale, IReadOnlyList<string>? Countries,
        IReadOnlyList<string>? ConfirmWhen, IReadOnlyList<string>? RefuteWhen, string? HandlingNote,
        HypothesisStatus Status = HypothesisStatus.Open, string? DecisionNote = null, string? PromotedToSlug = null);

    public sealed record FileIndicator(string Statement, EvidenceLevel Level, DateOnly? ObservedOn, EvidenceInput Evidence);

    public sealed record ImportOutcome(string Slug, bool Created, int ActorsLinked, int IndicatorsCreated, int IndicatorsExisting, IReadOnlyList<string> Notes);

    public async Task<ImportOutcome> ImportAsync(string json, string by, CancellationToken ct)
    {
        var file = JsonSerializer.Deserialize<HypothesisFile>(json, CaseImportService.Json) ?? throw new InvalidDataException("Empty hypothesis file.");
        var h = file.Hypothesis;
        var notes = new List<string>();

        var input = new HypothesisInput(h.Title, h.Question, h.Rationale, h.Countries, h.ConfirmWhen, h.RefuteWhen, h.HandlingNote, h.Slug);
        var existing = await db.Hypotheses.AsNoTracking().FirstOrDefaultAsync(x => x.Slug == h.Slug, ct).ConfigureAwait(false);
        var created = existing is null;
        if (created)
        {
            await hypotheses.CreateAsync(input, by, ct).ConfigureAwait(false);
        }
        else
        {
            await hypotheses.UpdateAsync(h.Slug, input, ct).ConfigureAwait(false);
        }

        // Related actors: existing ones only, by exact normalized name or alias.
        var actorIds = new List<Guid>();
        foreach (var name in file.Actors ?? [])
        {
            var normalized = NameNormalizer.Normalize(name);
            var actor = await db.Actors.AsNoTracking()
                .Where(a => a.NameNormalized == normalized || a.Aliases.Any(al => al.NameNormalized == normalized))
                .OrderByDescending(a => a.SanctionDesignationId != null)
                .ThenBy(a => a.CreatedAt)
                .FirstOrDefaultAsync(ct)
                .ConfigureAwait(false);
            if (actor is null)
            {
                notes.Add($"actor '{name}' not found; a hypothesis does not create actors");
                continue;
            }

            actorIds.Add(actor.Id);
            notes.Add($"actor '{name}' = {actor.Name}" + (actor.SanctionDesignationId is not null ? $" (sanctions {actor.SanctionSourceCode})" : string.Empty));
        }

        if (actorIds.Count > 0 || !created)
        {
            await hypotheses.SetActorsAsync(h.Slug, new HypothesisActorsInput(actorIds), by, ct).ConfigureAwait(false);
        }

        // Indicators, by statement.
        int indicatorsCreated = 0, indicatorsExisting = 0;
        var current = await db.Hypotheses.AsNoTracking().Include(x => x.Indicators).FirstAsync(x => x.Slug == h.Slug, ct).ConfigureAwait(false);
        foreach (var i in file.Indicators ?? [])
        {
            var statement = i.Statement.Trim();
            if (current.Indicators.Any(x => x.Statement == statement))
            {
                indicatorsExisting++;
                continue;
            }

            var view = await hypotheses.AddIndicatorAsync(h.Slug, new IndicatorInput(statement, i.Level, i.ObservedOn, i.Evidence), by, ct).ConfigureAwait(false);
            var added = view.Indicators.First(x => x.Statement == statement);
            if (!added.HasSnapshot)
            {
                notes.Add($"indicator {added.Id}: no snapshot of {added.Url} (page not fetched; the quote stands on its own)");
            }

            indicatorsCreated++;
        }

        // Status last; a file may carry a dismissed or promoted hypothesis for the record.
        if (h.Status != HypothesisStatus.Open && (existing is null || existing.Status != h.Status))
        {
            await hypotheses.DecideAsync(h.Slug, new HypothesisDecisionInput(h.Status, h.DecisionNote ?? "Imported from a file.", h.PromotedToSlug), by, ct).ConfigureAwait(false);
        }

        logger.LogInformation("Hypothesis {Slug}: {Created}, actors {Actors} linked, indicators {New} new / {Existing} existing.", h.Slug, created ? "created" : "updated", actorIds.Count, indicatorsCreated, indicatorsExisting);
        return new ImportOutcome(h.Slug, created, actorIds.Count, indicatorsCreated, indicatorsExisting, notes);
    }
}
