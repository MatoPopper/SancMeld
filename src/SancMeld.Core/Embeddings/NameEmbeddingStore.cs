using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;

namespace SancMeld.Core.Embeddings;

/// <summary>
/// Keeps designation_name_embedding complete for the active model.
///
/// One operation, idempotent: embed every current name that has no vector yet. The import
/// calls it after a commit and it does that day's delta; the backfill command calls it on
/// a fresh deployment and it does everything; a run that failed halfway leaves nothing to
/// undo, only names still waiting. Vectors of names that stopped being current are left in
/// place — a closed revision keeps its rows, and a past screening may still point at them.
/// </summary>
public sealed class NameEmbeddingStore(
    SancMeldDbContext db,
    INameEmbedder embedder,
    SemanticNameIndex index,
    IOptions<SancMeldOptions> options,
    TimeProvider clock,
    ILogger<NameEmbeddingStore> logger)
{
    /// <summary>Names fetched from the database per round. Independent of the request batch size.</summary>
    private const int PageSize = 1_000;

    /// <summary>
    /// Embeds what is missing, for one list or all of them. Returns how many vectors were
    /// written; zero when the mode is Off, which is how the import stays unaware of
    /// whether embeddings exist at all.
    /// </summary>
    public async Task<int> EmbedMissingAsync(
        string? sourceCode = null,
        IProgress<(int Done, int Total)>? progress = null,
        CancellationToken ct = default)
    {
        var settings = options.Value.Embeddings;

        if (settings.Mode == EmbeddingsMode.Off)
        {
            return 0;
        }

        var model = await ActiveModelAsync(ct).ConfigureAwait(false);

        var pending = db.Names
            .Where(n => n.Designation!.ValidTo == null)
            .Where(n => sourceCode == null || n.Designation!.Source!.Code == sourceCode)
            .Where(n => !db.NameEmbeddings.Any(e => e.NameId == n.Id && e.ModelId == model.Id));

        var total = await pending.CountAsync(ct).ConfigureAwait(false);
        var done = 0;
        progress?.Report((done, total));

        if (total == 0)
        {
            return 0;
        }

        // Keyed paging rather than Skip: every page written shrinks the pending set, so an
        // offset would skip every other page.
        long lastId = 0;

        while (true)
        {
            var page = await pending
                .Where(n => n.Id > lastId)
                .OrderBy(n => n.Id)
                .Take(PageSize)
                .Select(n => new { n.Id, n.NameOriginal })
                .ToListAsync(ct)
                .ConfigureAwait(false);

            if (page.Count == 0)
            {
                break;
            }

            lastId = page[^1].Id;

            foreach (var batch in page.Chunk(settings.BatchSize))
            {
                var vectors = await embedder
                    .EmbedAsync([.. batch.Select(n => settings.NamePrefix + n.NameOriginal)], ct)
                    .ConfigureAwait(false);

                var now = clock.GetUtcNow();

                for (var i = 0; i < batch.Length; i++)
                {
                    if (vectors[i].Length != model.Dimensions)
                    {
                        // The endpoint is serving a different model from the one the row
                        // describes. Storing these would mix two vector spaces in one table.
                        throw new EmbeddingUnavailableException(
                            $"Model '{embedder.ModelName}' returned {vectors[i].Length} dimensions; "
                            + $"embedding_model {model.Code} {model.Version} records {model.Dimensions}.");
                    }

                    db.NameEmbeddings.Add(new DesignationNameEmbedding
                    {
                        NameId = batch[i].Id,
                        ModelId = model.Id,
                        Vector = EmbeddingVector.ToBytes(vectors[i]),
                        CreatedAt = now,
                    });
                }

                await db.SaveChangesAsync(ct).ConfigureAwait(false);
                db.ChangeTracker.Clear();

                done += batch.Length;
                progress?.Report((done, total));
            }
        }

        if (done > 0)
        {
            // The in-memory copy is now behind the table.
            index.Invalidate();
            logger.LogInformation("Embedded {Count} names under {Model}.", done, model.Code);
        }

        return done;
    }

    /// <summary>How complete the table is, per list, for the active model.</summary>
    public async Task<IReadOnlyList<EmbeddingCoverage>> CoverageAsync(CancellationToken ct = default)
    {
        var model = await ActiveModelAsync(ct).ConfigureAwait(false);

        // Two plain group-counts rather than one with a correlated subquery in the
        // aggregate, which EF cannot translate.
        var names = await db.Names
            .Where(n => n.Designation!.ValidTo == null)
            .GroupBy(n => n.Designation!.Source!.Code)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var embedded = await db.NameEmbeddings
            .Where(e => e.ModelId == model.Id && e.Name!.Designation!.ValidTo == null)
            .GroupBy(e => e.Name!.Designation!.Source!.Code)
            .Select(g => new { Source = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Source, x => x.Count, ct)
            .ConfigureAwait(false);

        return names
            .OrderBy(n => n.Source, StringComparer.Ordinal)
            .Select(n => new EmbeddingCoverage(n.Source, n.Count, embedded.GetValueOrDefault(n.Source)))
            .ToList();
    }

    /// <summary>
    /// The row configuration points at. Its model name must be what the embedder runs —
    /// otherwise vectors would be written under a label that describes something else.
    /// </summary>
    public async Task<EmbeddingModel> ActiveModelAsync(CancellationToken ct = default)
    {
        var code = options.Value.Embeddings.ModelCode;

        var model = await db.EmbeddingModels
            .Where(m => m.IsActive && m.Code == code)
            .OrderByDescending(m => m.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException(
                $"No active row in embedding_model for code '{code}'. Every stored vector names the model "
                + "that produced it, so a model without a row cannot store any.");

        if (!string.Equals(model.ModelName, embedder.ModelName, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"embedding_model {model.Code} is '{model.ModelName}' but the embedder runs '{embedder.ModelName}'.");
        }

        return model;
    }
}

public sealed record EmbeddingCoverage(string SourceCode, int Names, int Embedded);
