using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Workspace;

/// <summary>
/// Claims and their evidence. The one rule the code enforces: no claim without a quote.
/// Everything else (the level, who is named, from what source) is the curator's call and
/// is shown as such; see EvidenceLevel.
/// </summary>
public sealed class ClaimService(HybridMeldDbContext db, PageCapture capture, TimeProvider clock)
{
    public async Task<ClaimView?> GetAsync(long id, CancellationToken ct) =>
        await Loaded().AsNoTracking().FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false) is { } claim
            ? Mapping.ToView(claim)
            : null;

    public async Task<ClaimView> CreateAsync(ClaimInput input, string by, FoundBy foundBy, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Statement))
        {
            throw WorkspaceException.Invalid("statement_required", "A claim is a sentence; write it.");
        }

        if (input.ObjectActorId == input.SubjectActorId)
        {
            throw WorkspaceException.Invalid("object_is_subject", "The subject and the object are the same actor.");
        }

        var campaign = await db.Campaigns.FirstOrDefaultAsync(c => c.Id == input.CampaignId, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.Invalid("campaign_unknown", "The case does not exist.");

        if (!await db.Actors.AnyAsync(a => a.Id == input.SubjectActorId, ct).ConfigureAwait(false))
        {
            throw WorkspaceException.Invalid("subject_unknown", "The subject actor does not exist.");
        }

        if (input.ObjectActorId is { } objectId && !await db.Actors.AnyAsync(a => a.Id == objectId, ct).ConfigureAwait(false))
        {
            throw WorkspaceException.Invalid("object_unknown", "The object actor does not exist.");
        }

        SourceItem? item = null;
        if (input.SourceItemId is { } itemId)
        {
            item = await db.SourceItems.Include(i => i.Source).FirstOrDefaultAsync(i => i.Id == itemId, ct).ConfigureAwait(false)
                ?? throw WorkspaceException.Invalid("item_unknown", "The inbox item does not exist.");
        }

        var evidence = input.Evidence ?? [];
        if (evidence.Count == 0 && item is null)
        {
            throw WorkspaceException.Invalid("evidence_required", "A claim needs at least one source with a verbatim quote.");
        }

        var now = clock.GetUtcNow();
        var claim = new Claim
        {
            CampaignId = campaign.Id,
            SubjectActorId = input.SubjectActorId,
            Predicate = input.Predicate,
            ObjectActorId = input.ObjectActorId,
            Statement = input.Statement.Trim(),
            Level = input.Level,
            Status = ClaimStatus.Candidate,
            FoundBy = foundBy,
            OccurredFrom = input.OccurredFrom,
            OccurredTo = input.OccurredTo,
            SourceItemId = item?.Id,
            CreatedBy = by,
            CreatedAt = now,
            UpdatedAt = now,
        };

        foreach (var e in evidence)
        {
            claim.Evidence.Add(await BuildEvidenceAsync(e, by, now, ct).ConfigureAwait(false));
        }

        // An inbox item with no evidence typed: the item is the evidence, its text the quote,
        // its snapshot already on disk.
        if (item is not null && claim.Evidence.Count == 0)
        {
            claim.Evidence.Add(new Evidence
            {
                Url = item.Url,
                Publisher = item.Source!.Name,
                Title = item.Title,
                Quote = Clip(item.Text ?? item.Title ?? item.Url, 4000),
                PublishedOn = item.PublishedAt is { } at ? DateOnly.FromDateTime(at.UtcDateTime) : null,
                RetrievedAt = item.FetchedAt,
                SnapshotPath = item.SnapshotPath,
                Sha256 = item.Sha256,
                AddedBy = by,
            });
        }

        if (item is not null)
        {
            item.Status = SourceItemStatus.Linked;
            item.TriagedBy ??= by;
            item.TriagedAt ??= now;
        }

        campaign.UpdatedAt = now;
        db.Claims.Add(claim);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(claim.Id, ct).ConfigureAwait(false))!;
    }

    public async Task<ClaimView> UpdateAsync(long id, ClaimUpdateInput input, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Statement))
        {
            throw WorkspaceException.Invalid("statement_required", "A claim is a sentence; write it.");
        }

        var claim = await db.Claims.Include(c => c.Campaign).FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The claim");

        if (input.ObjectActorId == claim.SubjectActorId)
        {
            throw WorkspaceException.Invalid("object_is_subject", "The subject and the object are the same actor.");
        }

        if (input.ObjectActorId is { } objectId && !await db.Actors.AnyAsync(a => a.Id == objectId, ct).ConfigureAwait(false))
        {
            throw WorkspaceException.Invalid("object_unknown", "The object actor does not exist.");
        }

        var now = clock.GetUtcNow();
        claim.Predicate = input.Predicate;
        claim.ObjectActorId = input.ObjectActorId;
        claim.Statement = input.Statement.Trim();
        claim.Level = input.Level;
        claim.OccurredFrom = input.OccurredFrom;
        claim.OccurredTo = input.OccurredTo;
        claim.UpdatedAt = now;
        claim.Campaign!.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    public async Task<ClaimView> AddEvidenceAsync(long id, EvidenceInput input, string by, CancellationToken ct)
    {
        var claim = await db.Claims.Include(c => c.Campaign).FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The claim");

        var now = clock.GetUtcNow();
        var evidence = await BuildEvidenceAsync(input, by, now, ct).ConfigureAwait(false);
        evidence.ClaimId = claim.Id;
        db.Evidence.Add(evidence);
        claim.UpdatedAt = now;
        claim.Campaign!.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Confirmed, Rejected, Withdrawn, or Candidate to take a decision back. Who decided
    /// is the signed-in address, never a form field. Every decision is appended to the
    /// audit table; the claim carries the latest.
    /// </summary>
    public async Task<ClaimView> DecideAsync(long id, ClaimDecisionInput input, string by, CancellationToken ct)
    {
        var claim = await db.Claims.Include(c => c.Campaign).FirstOrDefaultAsync(c => c.Id == id, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The claim");

        if (input.Decision == ClaimStatus.Withdrawn && string.IsNullOrWhiteSpace(input.Note))
        {
            throw WorkspaceException.Invalid("note_required", "Withdrawing a claim needs a note saying why.");
        }

        var now = clock.GetUtcNow();
        var note = string.IsNullOrWhiteSpace(input.Note) ? null : input.Note.Trim();
        db.ClaimDecisions.Add(new ClaimDecision
        {
            ClaimId = claim.Id,
            PreviousStatus = claim.Status,
            Decision = input.Decision,
            Note = note,
            DecidedBy = by,
            DecidedAt = now,
        });

        var cleared = input.Decision == ClaimStatus.Candidate;
        claim.Status = input.Decision;
        claim.DecidedBy = cleared ? null : by;
        claim.DecidedAt = cleared ? null : now;
        claim.DecisionNote = note;
        claim.UpdatedAt = now;
        claim.Campaign!.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(id, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The quote is required; the page is fetched and kept on a best-effort basis. A
    /// fetch that fails leaves SnapshotPath null and the claim is still saved: a paywalled
    /// or deleted page is exactly when the quote matters most.
    /// </summary>
    private async Task<Evidence> BuildEvidenceAsync(EvidenceInput input, string by, DateTimeOffset now, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(input.Quote))
        {
            throw WorkspaceException.Invalid("quote_required", "Evidence is a verbatim quote from the source; paste it.");
        }

        if (!Uri.TryCreate(input.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw WorkspaceException.Invalid("url_invalid", "Evidence needs an http(s) URL.");
        }

        var evidence = new Evidence
        {
            Url = uri.ToString(),
            ArchiveUrl = Trim(input.ArchiveUrl),
            Publisher = Trim(input.Publisher) ?? uri.Host,
            Title = Trim(input.Title),
            Quote = Clip(input.Quote.Trim(), 4000),
            PublishedOn = input.PublishedOn,
            Language = Trim(input.Language),
            RetrievedAt = now,
            AddedBy = by,
        };

        if (await capture.TryAsync(uri, "evidence", ct).ConfigureAwait(false) is { } page)
        {
            evidence.SnapshotPath = page.Path;
            evidence.Sha256 = page.Sha256;
            evidence.Title ??= page.Title;
        }

        return evidence;
    }

    private IQueryable<Claim> Loaded() => db.Claims
        .Include(c => c.Campaign)
        .Include(c => c.Subject)
        .Include(c => c.Object)
        .Include(c => c.Evidence)
        .AsSplitQuery();

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
