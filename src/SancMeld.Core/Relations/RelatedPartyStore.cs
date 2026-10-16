using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SancMeld.Core.Data;
using SancMeld.Core.Domain;
using SancMeld.Core.Matching;

namespace SancMeld.Core.Relations;

/// <summary>
/// Writes what a register connector found: which external record a listed subject is,
/// which parties that record is related to, and what each party is called.
///
/// A connector never touches the tables directly. It hands over plain records, and this
/// class decides what is new, what is unchanged and what has disappeared — the same
/// division the sanctions import has, for the same reason: the diff is where the bugs
/// would be, and it should exist once.
/// </summary>
public sealed class RelatedPartyStore(SancMeldDbContext db, TimeProvider clock, ILogger<RelatedPartyStore> logger)
{
    /// <summary>
    /// Listed persons a connector has not yet looked up, or looked up against a revision
    /// that has since changed, or looked up long enough ago to be worth another try.
    ///
    /// Only persons with a day-precise date of birth: the date is the one qualifier
    /// that makes a name match on an open register defensible. A name alone matches
    /// namesakes; a name and a birthday do not, outside of coincidence.
    /// </summary>
    public async Task<IReadOnlyList<PendingPerson>> PendingPersonsAsync(
        string system,
        TimeSpan retryAfter,
        int limit,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var retryBefore = now - retryAfter;

        var query =
            from d in db.Designations.AsNoTracking()
            where d.ValidTo == null
                  && d.Status != DesignationStatus.Delisted
                  && d.SubjectType == SubjectType.Person
                  && d.Births.Any(b => b.Precision == DatePrecision.Day && b.Year != null && b.Month != null && b.Day != null)
            let identity = db.ExternalIdentities
                .Where(i => i.System == system && i.SourceId == d.SourceId && i.SourceRecordKey == d.SourceRecordKey)
                .OrderBy(i => i.Id)
                .FirstOrDefault()
            where identity == null
                  || (identity.Status == LinkStatus.Candidate
                      && (identity.DesignationId != d.Id || (identity.ExternalId == null && identity.ResolvedAt < retryBefore)))
            orderby d.Id
            select new
            {
                d.Id,
                d.SourceId,
                d.SourceRecordKey,
                Births = d.Births
                    .Where(b => b.Precision == DatePrecision.Day && b.Year != null && b.Month != null && b.Day != null)
                    .Select(b => new { b.Year, b.Month, b.Day })
                    .ToList(),
                Names = d.Names.Where(n => !n.IsWeak).Select(n => n.NameOriginal).ToList(),
            };

        // Two collections per person (dates, names): split, or EF joins them into one
        // row set of dates × names and warns about it.
        var rows = await query.Take(limit).AsSplitQuery().ToListAsync(ct).ConfigureAwait(false);

        return rows
            .Select(r => new PendingPerson(
                r.Id,
                r.SourceId,
                r.SourceRecordKey,
                [.. r.Births.Select(b => new DateOnly(b.Year!.Value, b.Month!.Value, b.Day!.Value)).Distinct()],
                r.Names))
            .ToList();
    }

    /// <summary>
    /// Records the outcome of one lookup, found or not. A miss is recorded too, so the
    /// next run does not repeat it until <see cref="PendingPersonsAsync"/> says it is time.
    /// </summary>
    public async Task<ExternalIdentity> RecordIdentityAsync(
        PendingPerson person,
        string system,
        IdentityMatch? match,
        string? note,
        Guid runId,
        CancellationToken ct = default)
    {
        var identity = await db.ExternalIdentities
            .FirstOrDefaultAsync(i => i.System == system && i.SourceId == person.SourceId && i.SourceRecordKey == person.SourceRecordKey, ct)
            .ConfigureAwait(false);

        if (identity is null)
        {
            identity = new ExternalIdentity
            {
                System = system,
                SourceId = person.SourceId,
                SourceRecordKey = person.SourceRecordKey,
                DesignationId = person.DesignationId,
                Status = LinkStatus.Candidate,
            };
            db.ExternalIdentities.Add(identity);
        }

        if (identity.Status != LinkStatus.Candidate)
        {
            // A person decided. The machine's new opinion is not written over it; only
            // the revision it was checked against moves on.
            identity.DesignationId = person.DesignationId;
            identity.ResolvedAt = clock.GetUtcNow();
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return identity;
        }

        identity.DesignationId = person.DesignationId;
        identity.ExternalId = match?.ExternalId;
        identity.Label = match?.Label;
        identity.MatchedName = match?.MatchedName;
        identity.MatchedLabel = match?.MatchedLabel;
        identity.NameScore = match is null ? null : (decimal)Math.Round(match.NameScore, 5);
        identity.BirthDateMatched = match is not null;
        identity.Note = match?.Note ?? note;
        identity.ResolvedAt = clock.GetUtcNow();
        identity.RunId = runId;

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return identity;
    }

    /// <summary>Identities with an external record, due for their relations to be refreshed.</summary>
    public async Task<IReadOnlyList<ExternalIdentity>> IdentitiesDueForRelationsAsync(
        string system,
        TimeSpan refreshAfter,
        int limit,
        CancellationToken ct = default)
    {
        var before = clock.GetUtcNow() - refreshAfter;

        return await db.ExternalIdentities
            .Where(i => i.System == system && i.ExternalId != null && i.Status != LinkStatus.Rejected)
            .Where(i => i.RelationsFetchedAt == null || i.RelationsFetchedAt < before)
            .OrderBy(i => i.RelationsFetchedAt)
            .ThenBy(i => i.Id)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Replaces what the register currently says about one identity's relations: new
    /// statements are added, known ones have their last-seen time moved, and statements
    /// the register no longer makes are closed (kept, dated, not deleted — a screening
    /// may have shown them).
    /// </summary>
    public async Task UpsertRelationsAsync(
        ExternalIdentity identity,
        IReadOnlyList<RelationRecord> relations,
        Guid runId,
        CancellationToken ct = default)
    {
        var now = clock.GetUtcNow();
        var parties = await UpsertPartiesAsync(identity.System, relations.Select(r => r.Party), now, ct).ConfigureAwait(false);

        var existing = await db.DesignationRelations
            .Where(r => r.IdentityId == identity.Id)
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var seen = new HashSet<long>();

        foreach (var record in relations)
        {
            var partyId = parties[record.Party.ExternalId];
            var relation = existing.FirstOrDefault(r => r.RelatedPartyId == partyId && r.Kind == record.Kind);

            if (relation is null)
            {
                relation = new DesignationRelation
                {
                    IdentityId = identity.Id,
                    RelatedPartyId = partyId,
                    Kind = record.Kind,
                    Status = LinkStatus.Candidate,
                    FirstSeenAt = now,
                    RunId = runId,
                };
                db.DesignationRelations.Add(relation);
                existing.Add(relation);
            }

            relation.Property = record.Property;
            relation.StatementId = record.StatementId;
            relation.Share = record.Share;
            relation.StartDate = record.StartDate;
            relation.EndDate = record.EndDate;
            relation.EvidenceUrls = record.EvidenceUrls.Count == 0 ? null : System.Text.Json.JsonSerializer.Serialize(record.EvidenceUrls);
            relation.LastSeenAt = now;
            relation.IsCurrent = true;

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            seen.Add(relation.Id);
        }

        foreach (var gone in existing.Where(r => r.IsCurrent && !seen.Contains(r.Id)))
        {
            gone.IsCurrent = false;
        }

        identity.RelationsFetchedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    /// <summary>Returns party ids by external id, creating or refreshing each party and its names.</summary>
    private async Task<Dictionary<string, long>> UpsertPartiesAsync(
        string system,
        IEnumerable<PartyRecord> records,
        DateTimeOffset now,
        CancellationToken ct)
    {
        var result = new Dictionary<string, long>(StringComparer.Ordinal);

        foreach (var record in records.DistinctBy(p => p.ExternalId))
        {
            var party = await db.RelatedParties
                .Include(p => p.Names)
                .FirstOrDefaultAsync(p => p.System == system && p.ExternalId == record.ExternalId, ct)
                .ConfigureAwait(false);

            if (party is null)
            {
                party = new RelatedParty { System = system, ExternalId = record.ExternalId, Label = record.Label };
                db.RelatedParties.Add(party);
            }

            party.Label = record.Label;
            party.Description = record.Description;
            party.Kind = record.Kind;
            party.CountryIso2 = record.CountryIso2;
            party.Identifiers = record.Identifiers.Count == 0
                ? null
                : System.Text.Json.JsonSerializer.Serialize(record.Identifiers);
            party.RetrievedAt = now;

            // Names are compared by their normal form: the register carries the same
            // spelling under several language codes, and the index needs it once.
            var wanted = record.Names
                .Where(n => !string.IsNullOrWhiteSpace(n))
                .Select(n => n.Trim())
                .DistinctBy(NameNormalizer.Normalize, StringComparer.Ordinal)
                .ToList();

            var known = party.Names.Select(n => NameNormalizer.Normalize(n.NameOriginal)).ToHashSet(StringComparer.Ordinal);

            foreach (var name in wanted)
            {
                if (known.Add(NameNormalizer.Normalize(name)))
                {
                    party.Names.Add(new RelatedPartyName { NameOriginal = name });
                }
            }

            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            result[record.ExternalId] = party.Id;
        }

        return result;
    }

    public async Task<RelationRun> StartRunAsync(string system, CancellationToken ct = default)
    {
        var run = new RelationRun { Id = Guid.CreateVersion7(), System = system, StartedAt = clock.GetUtcNow(), Status = "RUNNING" };
        db.RelationRuns.Add(run);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return run;
    }

    public async Task FinishRunAsync(RelationRun run, string status, string? error, CancellationToken ct = default)
    {
        run.Status = status;
        run.FinishedAt = clock.GetUtcNow();
        run.ErrorSummary = error is { Length: > 2000 } ? error[..2000] : error;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        logger.LogInformation(
            "{System}: {Status} — {Attempted} persons looked up, {Resolved} resolved, {Relations} relations across {Parties} parties.",
            run.System, status, run.PersonsAttempted, run.PersonsResolved, run.RelationsSeen, run.PartiesSeen);
    }

    /// <summary>How far the register coverage has got, for the status command.</summary>
    public async Task<RelationCoverage> CoverageAsync(string system, CancellationToken ct = default)
    {
        var identities = db.ExternalIdentities.Where(i => i.System == system);

        return new RelationCoverage(
            Attempted: await identities.CountAsync(ct).ConfigureAwait(false),
            Resolved: await identities.CountAsync(i => i.ExternalId != null, ct).ConfigureAwait(false),
            Confirmed: await identities.CountAsync(i => i.Status == LinkStatus.Confirmed, ct).ConfigureAwait(false),
            Rejected: await identities.CountAsync(i => i.Status == LinkStatus.Rejected, ct).ConfigureAwait(false),
            Parties: await db.RelatedParties.CountAsync(p => p.System == system, ct).ConfigureAwait(false),
            Relations: await db.DesignationRelations.CountAsync(r => r.IsCurrent && r.Identity!.System == system, ct).ConfigureAwait(false),
            PendingPersons: (await PendingPersonsAsync(system, TimeSpan.FromDays(30), int.MaxValue, ct).ConfigureAwait(false)).Count);
    }
}

/// <summary>A listed person as a connector needs to see them: every day-precise birth date, every spelling.</summary>
public sealed record PendingPerson(
    Guid DesignationId,
    Guid SourceId,
    string SourceRecordKey,
    IReadOnlyList<DateOnly> BirthDates,
    IReadOnlyList<string> Names);

/// <summary>Which external record a listed person was decided to be, and why.</summary>
public sealed record IdentityMatch(
    string ExternalId,
    string? Label,
    string MatchedName,
    string MatchedLabel,
    double NameScore,
    string? Note = null);

public sealed record PartyRecord(
    string ExternalId,
    string Label,
    string? Description,
    RelatedPartyKind Kind,
    string? CountryIso2,
    IReadOnlyList<RelatedPartyIdentifierView> Identifiers,
    IReadOnlyList<string> Names);

public sealed record RelationRecord(
    PartyRecord Party,
    RelationKind Kind,
    string Property,
    string? StatementId,
    decimal? Share,
    DateOnly? StartDate,
    DateOnly? EndDate,
    IReadOnlyList<string> EvidenceUrls);

public sealed record RelationCoverage(
    int Attempted, int Resolved, int Confirmed, int Rejected, int Parties, int Relations, int PendingPersons);
