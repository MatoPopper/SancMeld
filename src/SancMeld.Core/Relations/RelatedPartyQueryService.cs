using System.Globalization;
using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;
using SancMeld.Core.Search;

namespace SancMeld.Core.Relations;

/// <summary>
/// Reading related parties: by a name that resembles the query, or everything one listed
/// subject is related to.
///
/// A party is only served through a relation that still stands, to an identity nobody
/// has rejected, on a listing that is current and not delisted. A company whose owner
/// was delisted yesterday stops being a finding today, for the same reason the owner did.
/// </summary>
public sealed class RelatedPartyQueryService(
    SancMeldDbContext db,
    DesignationQueryService designations,
    IOptions<SancMeldOptions> options)
{
    public const int MaxHits = 50;

    /// <summary>
    /// Parties whose name the trigram index considers similar to the query — the same
    /// operators, the same thresholds and the same normal form as the listing search, so
    /// a name reaches a related company exactly as easily as it reaches a listing.
    /// </summary>
    public async Task<IReadOnlyList<RelatedPartyHit>> SearchAsync(string query, int limit = 10, CancellationToken ct = default)
    {
        var normalized = NameNormalizer.Normalize(query);
        if (normalized.Length == 0)
        {
            return [];
        }

        var take = Math.Clamp(limit, 1, MaxHits);
        var matching = options.Value.Matching;

        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var threshold = matching.SimilarityThreshold.ToString(CultureInfo.InvariantCulture);
        await db.Database
            .ExecuteSqlAsync($"SELECT set_config('pg_trgm.similarity_threshold', {threshold}, true)", ct)
            .ConfigureAwait(false);

        var byWord = normalized.Length >= matching.MinWordSimilarityQueryLength;
        if (byWord)
        {
            var wordThreshold = matching.WordSimilarityThreshold.ToString(CultureInfo.InvariantCulture);
            await db.Database
                .ExecuteSqlAsync($"SELECT set_config('pg_trgm.word_similarity_threshold', {wordThreshold}, true)", ct)
                .ConfigureAwait(false);
        }

        var names = db.RelatedPartyNames
            .Where(n => n.Party!.Relations.Any(r => r.IsCurrent && r.Status != LinkStatus.Rejected
                && r.Identity!.ExternalId != null && r.Identity.Status != LinkStatus.Rejected))
            .Where(n => EF.Functions.TrigramsAreSimilar(n.NameNormalized!, normalized)
                || (byWord && EF.Functions.TrigramsAreWordSimilar(normalized, n.NameNormalized!)));

        // Written out twice rather than through a helper, for the reason given in
        // DesignationQueryService: EF translates the expression, not a method call.
        var scored = byWord
            ? names.Select(n => new
            {
                n.RelatedPartyId,
                n.NameOriginal,
                Score = EF.Functions.TrigramsSimilarity(n.NameNormalized!, normalized)
                        > EF.Functions.TrigramsWordSimilarity(normalized, n.NameNormalized!)
                    ? EF.Functions.TrigramsSimilarity(n.NameNormalized!, normalized)
                    : EF.Functions.TrigramsWordSimilarity(normalized, n.NameNormalized!),
            })
            : names.Select(n => new
            {
                n.RelatedPartyId,
                n.NameOriginal,
                Score = EF.Functions.TrigramsSimilarity(n.NameNormalized!, normalized),
            });

        var matched = await scored.ToListAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);

        if (matched.Count == 0)
        {
            return [];
        }

        // A party carries its name in dozens of languages; it is ranked by the best one.
        var best = matched
            .GroupBy(m => m.RelatedPartyId)
            .Select(g => g.OrderByDescending(m => m.Score).First())
            .OrderByDescending(m => m.Score)
            .ThenBy(m => m.RelatedPartyId)
            .Take(take)
            .ToList();

        var ids = best.Select(b => b.RelatedPartyId).ToList();

        var parties = await db.RelatedParties
            .AsNoTracking()
            .AsSplitQuery()
            .Include(p => p.Names)
            .Include(p => p.Relations.Where(r => r.IsCurrent && r.Status != LinkStatus.Rejected))
                .ThenInclude(r => r.Identity)
            .Where(p => ids.Contains(p.Id))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var listings = await CurrentListingsAsync(
            parties.SelectMany(p => p.Relations).Select(r => r.Identity!), ct).ConfigureAwait(false);

        var hits = new List<RelatedPartyHit>(best.Count);

        foreach (var b in best)
        {
            var party = parties.FirstOrDefault(p => p.Id == b.RelatedPartyId);
            if (party is null)
            {
                continue;
            }

            var relations = party.Relations
                .Where(r => r.Identity!.ExternalId != null && r.Identity.Status != LinkStatus.Rejected)
                .Select(r => (Relation: r, Listing: listings.GetValueOrDefault((r.Identity!.SourceId, r.Identity.SourceRecordKey))))
                .Where(x => x.Listing is not null)
                .Select(x => ToView(x.Relation, x.Listing!))
                .OrderBy(r => r.Designation.Source, StringComparer.Ordinal)
                .ToList();

            if (relations.Count == 0)
            {
                // Every owner has been delisted or superseded: nothing to flag.
                continue;
            }

            hits.Add(new RelatedPartyHit
            {
                Party = ToView(party),
                MatchedName = b.NameOriginal,
                NameScore = b.Score,
                Relations = relations,
            });
        }

        return hits;
    }

    /// <summary>Everything a listed subject is related to, through whichever identity its record resolved to.</summary>
    public async Task<DesignationRelationsView> ForDesignationAsync(Guid designationId, CancellationToken ct = default)
    {
        var record = await db.Designations
            .AsNoTracking()
            .Where(d => d.Id == designationId)
            .Select(d => new { d.SourceId, d.SourceRecordKey })
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

        if (record is null)
        {
            return new DesignationRelationsView();
        }

        var relations = await db.DesignationRelations
            .AsNoTracking()
            .Include(r => r.Identity)
            .Include(r => r.Party!).ThenInclude(p => p.Names)
            .Where(r => r.IsCurrent && r.Status != LinkStatus.Rejected)
            .Where(r => r.Identity!.SourceId == record.SourceId && r.Identity.SourceRecordKey == record.SourceRecordKey)
            .Where(r => r.Identity!.ExternalId != null && r.Identity.Status != LinkStatus.Rejected)
            .OrderBy(r => r.Kind).ThenBy(r => r.Party!.Label)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new DesignationRelationsView
        {
            Relations =
            [
                .. relations.Select(r => new DesignationRelationView
                {
                    Party = ToView(r.Party!),
                    Kind = r.Kind,
                    Status = r.Status,
                    IdentityStatus = r.Identity!.Status,
                    Share = r.Share,
                    StartDate = r.StartDate,
                    EndDate = r.EndDate,
                    EvidenceUrls = Urls(r.EvidenceUrls),
                    LastSeenAt = r.LastSeenAt,
                }),
            ],
        };
    }

    /// <summary>The current, not delisted listing behind each identity, keyed by the publisher's record.</summary>
    internal async Task<Dictionary<(Guid SourceId, string Key), DesignationView>> CurrentListingsAsync(
        IEnumerable<ExternalIdentity> identities,
        CancellationToken ct)
    {
        var wanted = identities.Select(i => (i.SourceId, i.SourceRecordKey)).Distinct().ToList();
        if (wanted.Count == 0)
        {
            return [];
        }

        var keys = wanted.Select(w => w.SourceRecordKey).Distinct().ToList();

        var views = await DesignationQueryService
            .Hydrate(designations.CurrentRevisions()
                .Where(d => d.Status != DesignationStatus.Delisted)
                .Where(d => keys.Contains(d.SourceRecordKey)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var sourceIds = await db.Sources
            .Where(s => views.Select(v => v.Source).Contains(s.Code))
            .Select(s => new { s.Id, s.Code })
            .ToDictionaryAsync(s => s.Code, s => s.Id, ct)
            .ConfigureAwait(false);

        var result = new Dictionary<(Guid, string), DesignationView>();
        foreach (var view in views)
        {
            if (sourceIds.TryGetValue(view.Source, out var sourceId) && wanted.Contains((sourceId, view.SourceRecordKey)))
            {
                result[(sourceId, view.SourceRecordKey)] = view;
            }
        }

        return result;
    }

    private static RelationView ToView(DesignationRelation r, DesignationView listing) => new()
    {
        Id = r.Id,
        Kind = r.Kind,
        Status = r.Status,
        IdentityStatus = r.Identity!.Status,
        IdentityExternalId = r.Identity.ExternalId!,
        IdentityUrl = RecordUrl(r.Identity.System, r.Identity.ExternalId!),
        MatchedName = r.Identity.MatchedName,
        MatchedLabel = r.Identity.MatchedLabel,
        NameScore = r.Identity.NameScore is { } s ? (double)s : null,
        BirthDateMatched = r.Identity.BirthDateMatched,
        Share = r.Share,
        StartDate = r.StartDate,
        EndDate = r.EndDate,
        EvidenceUrls = Urls(r.EvidenceUrls),
        FirstSeenAt = r.FirstSeenAt,
        LastSeenAt = r.LastSeenAt,
        Designation = listing,
    };

    public static RelatedPartyView ToView(RelatedParty p) => new()
    {
        Id = p.Id,
        System = p.System,
        ExternalId = p.ExternalId,
        Url = RecordUrl(p.System, p.ExternalId),
        Name = p.Label,
        Description = p.Description,
        Kind = p.Kind,
        CountryIso2 = p.CountryIso2,
        Identifiers = p.Identifiers is null
            ? []
            : JsonSerializer.Deserialize<List<RelatedPartyIdentifierView>>(p.Identifiers) ?? [],
        Names = [.. p.Names.Select(n => n.NameOriginal).OrderBy(n => n, StringComparer.Ordinal)],
        RetrievedAt = p.RetrievedAt,
    };

    /// <summary>Where a reader can open the record. Only one register so far; a second adds a line here.</summary>
    public static string RecordUrl(string system, string externalId) => system switch
    {
        RelationSystems.Wikidata => $"https://www.wikidata.org/wiki/{externalId}",
        _ => externalId,
    };

    private static IReadOnlyList<string> Urls(string? json) =>
        json is null ? [] : JsonSerializer.Deserialize<List<string>>(json) ?? [];
}
