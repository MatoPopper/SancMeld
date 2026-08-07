using System.Globalization;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SancMeld.Core.Configuration;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Core.Search;

/// <summary>
/// Reading the aggregated list: everything, one record, or by name.
///
/// All three read only current revisions — <c>valid_to IS NULL</c>. History is reachable
/// through the revision chain, but a caller asking "who is listed" means today.
/// </summary>
public sealed class DesignationQueryService(SancMeldDbContext db, IOptions<SancMeldOptions> options)
{
    public const int MaxPageSize = 500;
    public const int DefaultPageSize = 100;

    /// <summary>
    /// Every current listing, in id order, for callers mirroring the aggregate.
    ///
    /// Filters narrow by provenance and kind, never by how much is known about a subject:
    /// a listing with nothing but a name is exactly as binding as a complete one.
    /// </summary>
    public async Task<Page<DesignationView>> GetAllAsync(
        Guid? cursor = null,
        int pageSize = DefaultPageSize,
        string? sourceCode = null,
        SubjectType? subjectType = null,
        bool includePossiblyMissing = true,
        CancellationToken ct = default)
    {
        var size = Math.Clamp(pageSize, 1, MaxPageSize);

        var query = CurrentRevisions();

        if (sourceCode is not null)
        {
            query = query.Where(d => d.Source!.Code == sourceCode);
        }

        if (subjectType is not null)
        {
            query = query.Where(d => d.SubjectType == subjectType);
        }

        if (!includePossiblyMissing)
        {
            query = query.Where(d => d.Status != DesignationStatus.PossiblyMissing);
        }

        if (cursor is not null)
        {
            query = query.Where(d => d.Id > cursor);
        }

        // One extra row answers "is there more" without a second count over the table.
        var rows = await Hydrate(query.OrderBy(d => d.Id).Take(size + 1))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var hasMore = rows.Count > size;
        var items = hasMore ? rows[..size] : rows;

        return new Page<DesignationView>(items, hasMore ? items[^1].Id : null);
    }

    public async Task<DesignationView?> GetAsync(Guid id, CancellationToken ct = default) =>
        await Hydrate(CurrentRevisions().Where(d => d.Id == id))
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);

    /// <summary>
    /// Trigram search over every spelling on record — primary names, aliases and
    /// transliterations alike.
    ///
    /// Character-level rather than word-level on purpose: 19 % of genuinely matching name
    /// pairs on the EU list share no exact token, so a tokenised index cannot reach them
    /// however it is tuned. Measured against the full list, this reaches 100 % recall@10.
    /// </summary>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(
        string query,
        int limit = 25,
        CancellationToken ct = default)
    {
        var normalized = NameNormalizer.Normalize(query);
        if (normalized.Length == 0)
        {
            return [];
        }

        var take = Math.Clamp(limit, 1, MaxPageSize);
        var matching = options.Value.Matching;

        // The % operator reads pg_trgm.similarity_threshold, and it is the only form that
        // uses the GIN index. The setting is made local to this transaction so it cannot
        // leak to the next request that borrows the same pooled connection.
        //
        // set_config rather than SET LOCAL because SET takes no parameters, and building
        // the statement by string concatenation to work around that is how a threshold
        // from configuration turns into an injection point.
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        var threshold = matching.SimilarityThreshold.ToString(CultureInfo.InvariantCulture);
        await db.Database
            .ExecuteSqlAsync($"SELECT set_config('pg_trgm.similarity_threshold', {threshold}, true)", ct)
            .ConfigureAwait(false);

        var candidateNames = MatchingNames(normalized, matching.ScoreWeakAliases);

        // A subject carries up to 145 spellings, so it is ranked by its best one. Grouping
        // in SQL rather than after the fact: taking the top N *names* would return far
        // fewer than N subjects whenever one subject holds several good spellings.
        var best = await candidateNames
            .GroupBy(n => n.DesignationId)
            .Select(g => new
            {
                DesignationId = g.Key,
                Score = g.Max(n => EF.Functions.TrigramsSimilarity(n.NameNormalized!, normalized)),
            })
            .OrderByDescending(x => x.Score)
            .ThenBy(x => x.DesignationId)
            .Take(take)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        if (best.Count == 0)
        {
            return [];
        }

        var ids = best.Select(b => b.DesignationId).ToList();

        var matchedNames = await candidateNames
            .Where(n => ids.Contains(n.DesignationId))
            .Select(n => new
            {
                n.DesignationId,
                n.NameOriginal,
                Score = EF.Functions.TrigramsSimilarity(n.NameNormalized!, normalized),
            })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var designations = await Hydrate(CurrentRevisions().Where(d => ids.Contains(d.Id)))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        await transaction.CommitAsync(ct).ConfigureAwait(false);

        var byId = designations.ToDictionary(d => d.Id);
        var bestName = matchedNames
            .GroupBy(n => n.DesignationId)
            .ToDictionary(g => g.Key, g => g.OrderByDescending(n => n.Score).First());

        return
        [
            .. best
                .Where(b => byId.ContainsKey(b.DesignationId))
                .Select(b => new SearchHit(byId[b.DesignationId], bestName[b.DesignationId].NameOriginal, b.Score)),
        ];
    }

    /// <summary>
    /// Names on current revisions that the trigram index considers similar.
    ///
    /// TrigramsAreSimilar is the % operator, and it is the only form the GIN index can
    /// serve. Writing the comparison as similarity(...) &gt;= x instead reads the same and
    /// scans every name in the table.
    /// </summary>
    private IQueryable<DesignationName> MatchingNames(string normalizedQuery, bool includeWeak) =>
        db.Names
            .Where(n => n.Designation!.ValidTo == null)
            .Where(n => includeWeak || !n.IsWeak)
            .Where(n => EF.Functions.TrigramsAreSimilar(n.NameNormalized!, normalizedQuery));

    internal IQueryable<Designation> CurrentRevisions() =>
        db.Designations.AsNoTracking().Where(d => d.ValidTo == null);

    /// <summary>
    /// Projected rather than Include()d: an Include over eight child collections multiplies
    /// rows before the database has finished deciding which designations it even needs.
    /// </summary>
    internal static IQueryable<DesignationView> Hydrate(IQueryable<Designation> designations) =>
        designations.Select(d => new DesignationView
        {
            Id = d.Id,
            Source = d.Source!.Code,
            LegalEffect = d.Source.LegalEffect,
            SourceRecordKey = d.SourceRecordKey,
            ExternalId = d.ExternalId,
            SubjectType = d.SubjectType,
            Status = d.Status,
            ListedAt = d.ListedAt,
            Function = d.Function,
            Title = d.Title,
            Remarks = d.Remarks,
            ValidFrom = d.ValidFrom,

            Names = d.Names
                .OrderBy(n => n.Kind == NameType.Primary ? 0 : 1)
                .ThenBy(n => n.Id)
                .Select(n => new NameView(n.NameOriginal, n.Kind, n.LanguageCode, n.IsWeak))
                .ToList(),

            Births = d.Births
                .Select(b => new BirthView(
                    b.Year, b.Month, b.Day, b.YearFrom, b.YearTo,
                    b.Precision, b.City, b.Region, b.CountryIso2, b.CountryOriginal))
                .ToList(),

            Identifiers = d.Identifiers
                .Select(i => new IdentifierView(
                    i.Kind, i.ValueOriginal, i.IssuingCountry, i.IssuedAt, i.ExpiresAt, i.IsKnownFalse, i.IsExpired))
                .ToList(),

            Addresses = d.Addresses
                .Select(a => new AddressView(
                    a.Street, a.PoBox, a.City, a.PostalCode, a.Region, a.CountryIso2, a.CountryOriginal, a.IsPartial))
                .ToList(),

            Nationalities = d.Nationalities
                .Select(n => new NationalityView(n.CountryIso2, n.CountryOriginal))
                .ToList(),

            Measures = d.Measures.Select(m => m.Kind).ToList(),
            Programmes = d.Programmes.Select(p => p.Programme!.Code).ToList(),

            CrossReferences = d.CrossReferences
                .Select(x => new CrossReferenceView(x.Kind, x.TargetSourceCode, x.TargetExternalId))
                .ToList(),

            LegalBasis = d.LegalInstruments
                .OrderBy(l => l.Relation)
                .Select(l => new LegalBasisView(
                    l.Relation,
                    l.LegalInstrument!.InstrumentType,
                    l.LegalInstrument.InstrumentNumber,
                    l.LegalInstrument.Title,
                    l.LegalInstrument.PublicationDate,
                    l.LegalInstrument.EffectiveDate,
                    l.LegalInstrument.OfficialUrl,
                    l.LegalInstrument.CelexNumber))
                .ToList(),

            Provenance = new ProvenanceView(
                d.Source.Name,
                d.Source.Publisher,
                d.Source.LandingPageUrl,
                d.Snapshot!.RetrievedAt,
                d.Snapshot.Sha256,
                d.Snapshot.ExternalVersion),
        });
}
