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
/// <param name="Person">The person as the source records them.</param>
/// <param name="Source">The list they come from.</param>
/// <param name="Functions">Their functions, in the country asked about.</param>
/// <param name="Score">Trigram similarity of the whole name, lifted for a containment of two or more words.</param>
/// <param name="Contained">
/// Every word of the query is a whole word of the stored name, or the initial of one:
/// "Fico" and "R. Fico" in "Robert Fico". Not enough on its own, a surname is shared by
/// thousands; enough together with a date of birth the source confirms.
/// </param>
/// <param name="RetrievedAt">When the import that last saw the person finished.</param>
public sealed record ListMatch(PepPerson Person, PepSource Source, IReadOnlyList<PepFunction> Functions, double Score, bool Contained, DateTimeOffset? RetrievedAt);

/// <summary>
/// Finds the stored persons whose name is close to the query. The date of birth only
/// narrows the field (a person recorded with another birthday is no candidate); whether a
/// candidate is the same person is the stage's to say, so each can be tested alone.
///
/// Two trigram operators, both served by the GIN index, as in SancMeld's search: % against
/// the whole stored name, and the word-similarity form for a query that is a part of a
/// longer name. Candidates are then re-scored in C# with the containment rule, and
/// only those at or above the threshold are returned, best first.
/// </summary>
public sealed class PepListMatcher(PepMeldDbContext db, IOptions<PepMeldOptions> options)
{
    /// <param name="name">The name as typed, titles and all.</param>
    /// <param name="country">Only persons with a function in this country; null for both.</param>
    /// <param name="ct">Cancels the queries.</param>
    /// <param name="birthDate">
    /// When given, a person whose source records another birthday is not a candidate at
    /// all. Filtered in the query, not after it: candidates are capped, and a surname
    /// shared by a hundred deputies must not push out the one born on the day asked about.
    /// </param>
    public async Task<IReadOnlyList<ListMatch>> FindAsync(string name, Country? country, CancellationToken ct, DateOnly? birthDate = null)
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
            .Where(p => birthDate == null || p.BirthDate == null || p.BirthDate == birthDate)
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
            var contained = Contains(normalized, person.NameNormalized);

            // Below the threshold and not contained: a different name that merely shares
            // trigrams. Contained but below it: a surname alone, which the stage keeps only
            // when the source's date of birth agrees.
            if (score < matching.SimilarityThreshold && !contained)
            {
                continue;
            }

            result.Add(new ListMatch(
                person,
                person.Source!,
                functions.Where(f => f.PersonId == person.Id).ToList(),
                score,
                contained,
                retrieved.TryGetValue(person.LastSeenRunId, out var at) ? at : null));
        }

        return result.OrderByDescending(m => m.Score).ToList();
    }

    /// <summary>
    /// Every word of the query is a word of the stored name, or a single letter that starts
    /// one, each stored word used once. Order does not matter: lists write the surname first.
    /// </summary>
    internal static bool Contains(string normalizedQuery, string normalizedStored)
    {
        var queryWords = normalizedQuery.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (queryWords.Length == 0 || queryWords.All(w => w.Length == 1))
        {
            // Initials alone name nobody.
            return false;
        }

        var stored = normalizedStored.Split(' ', StringSplitOptions.RemoveEmptyEntries).ToList();

        // Whole words first, so an initial cannot take the word a full word needs.
        foreach (var word in queryWords.Where(w => w.Length > 1))
        {
            if (!stored.Remove(word))
            {
                return false;
            }
        }

        foreach (var initial in queryWords.Where(w => w.Length == 1))
        {
            var index = stored.FindIndex(w => w.StartsWith(initial, StringComparison.Ordinal));
            if (index < 0)
            {
                return false;
            }

            stored.RemoveAt(index);
        }

        return true;
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
