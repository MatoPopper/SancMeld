using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PepMeld.Core.Configuration;
using PepMeld.Core.Data;
using PepMeld.Core.Domain;
using PepMeld.Core.Matching;
using SancMeld.Core.Matching;

namespace PepMeld.Core.Checks;

/// <summary>A stored person who may be the one asked about, with their functions and the import that last saw them.</summary>
public sealed record ListMatch(PepPerson Person, PepSource Source, IReadOnlyList<PepFunction> Functions, double Score, DateTimeOffset? RetrievedAt);

/// <summary>
/// Finds the stored persons whose name is close to the query. Nothing here looks at the
/// date of birth: the matcher says who has that name, the stage says whether it is the
/// same person, and the two questions are kept apart so each can be tested alone.
///
/// Two trigram operators, both served by the GIN index, as in SancMeld's search: % against
/// the whole stored name, and the word-similarity form for a query that is a part of a
/// longer name. Candidates are then re-scored in C# with the containment rule, and
/// only those at or above the threshold are returned, best first.
/// </summary>
public sealed class PepListMatcher(PepMeldDbContext db, IOptions<PepMeldOptions> options)
{
    public async Task<IReadOnlyList<ListMatch>> FindAsync(string name, Country? country, CancellationToken ct)
    {
        var normalized = PersonNames.Normalize(name);
        if (normalized.Length == 0)
        {
            return [];
        }

        var matching = options.Value.Matching;
        var threshold = matching.SimilarityThreshold.ToString("0.00", CultureInfo.InvariantCulture);

        // set_config(..., true) lasts for the transaction, so there has to be one; without
        // it the setting would expire with the statement that set it.
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('pg_trgm.similarity_threshold', {threshold}, true)", ct).ConfigureAwait(false);
        await db.Database.ExecuteSqlAsync($"SELECT set_config('pg_trgm.word_similarity_threshold', {threshold}, true)", ct).ConfigureAwait(false);

        var candidates = await db.Persons.AsNoTracking()
            .Where(p => EF.Functions.TrigramsAreSimilar(p.NameNormalized, normalized)
                        || EF.Functions.TrigramsAreWordSimilar(normalized, p.NameNormalized))
            .Where(p => country == null || p.Functions.Any(f => f.Country == country))
            .OrderByDescending(p => EF.Functions.TrigramsSimilarity(p.NameNormalized, normalized))
            .Take(matching.MaxCandidates)
            .Include(p => p.Source)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (candidates.Count == 0)
        {
            return [];
        }

        var ids = candidates.Select(p => p.Id).ToArray();
        var functions = await db.Functions.AsNoTracking()
            .Where(f => ids.Contains(f.PersonId))
            .Where(f => country == null || f.Country == country)
            .OrderByDescending(f => f.ValidTo == null)
            .ThenByDescending(f => f.ValidFrom)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var runIds = candidates.Select(p => p.LastSeenRunId).Distinct().ToArray();
        var retrieved = await db.ImportRuns.AsNoTracking()
            .Where(r => runIds.Contains(r.Id))
            .ToDictionaryAsync(r => r.Id, r => r.FinishedAt ?? r.StartedAt, ct)
            .ConfigureAwait(false);

        var result = new List<ListMatch>(candidates.Count);
        foreach (var person in candidates)
        {
            var score = Score(normalized, person.NameNormalized, matching.ContainmentScore);
            if (score < matching.SimilarityThreshold)
            {
                continue;
            }

            result.Add(new ListMatch(
                person,
                person.Source!,
                functions.Where(f => f.PersonId == person.Id).ToList(),
                score,
                retrieved.TryGetValue(person.LastSeenRunId, out var at) ? at : null));
        }

        return result.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>
    /// Trigram similarity, lifted to <paramref name="containmentScore"/> when every word of
    /// the query is a whole word of the stored name and there are at least two of them:
    /// one word is a surname, and a surname alone is not a containment worth the lift.
    /// </summary>
    internal static double Score(string normalizedQuery, string normalizedStored, double containmentScore)
    {
        var score = TrigramSimilarity.Similarity(normalizedQuery, normalizedStored);

        var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryWords.Length >= 2)
        {
            var storedWords = normalizedStored.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            if (queryWords.All(storedWords.Contains))
            {
                score = Math.Max(score, containmentScore);
            }
        }

        return score;
    }
}
