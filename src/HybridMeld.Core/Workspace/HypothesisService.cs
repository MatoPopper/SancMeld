using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Sources;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Workspace;

/// <summary>
/// Hypotheses: questions with indicators, kept apart from cases. Everyone with the
/// grant reads and writes them, as with cases. They are not in the feed, the facets or
/// the graph by construction: nothing here touches a claim.
/// </summary>
public sealed class HypothesisService(HybridMeldDbContext db, PageCapture capture, TimeProvider clock)
{
    public async Task<Page<HypothesisSummary>> ListAsync(HypothesisStatus? status, string? q, int limit, int offset, CancellationToken ct)
    {
        var query = Loaded().AsNoTracking();
        if (status is { } s)
        {
            query = query.Where(h => h.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(h => EF.Functions.ILike(h.Title, pattern) || EF.Functions.ILike(h.Question, pattern) || EF.Functions.ILike(h.Rationale, pattern));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(h => h.UpdatedAt)
            .Skip(Math.Max(0, offset))
            .Take(Math.Clamp(limit, 1, 200))
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new Page<HypothesisSummary>(items.Select(Mapping.ToSummary).ToList(), total);
    }

    public async Task<HypothesisView?> GetAsync(string slug, CancellationToken ct) =>
        await Loaded().AsNoTracking().AsSplitQuery().FirstOrDefaultAsync(h => h.Slug == slug, ct).ConfigureAwait(false) is { } h
            ? Mapping.ToView(h)
            : null;

    /// <summary>The hypotheses that point at an actor, for the actor's page; a pointer, not a tie.</summary>
    public async Task<IReadOnlyList<HypothesisSummary>> ForActorAsync(Guid actorId, CancellationToken ct) =>
        (await Loaded().AsNoTracking()
            .Where(h => h.Actors.Any(a => a.ActorId == actorId))
            .OrderByDescending(h => h.UpdatedAt)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false))
        .Select(Mapping.ToSummary)
        .ToList();

    public async Task<HypothesisView> CreateAsync(HypothesisInput input, string by, CancellationToken ct)
    {
        Validate(input);
        var slug = CampaignService.Slugify(string.IsNullOrWhiteSpace(input.Slug) ? input.Title : input.Slug);
        if (slug.Length == 0)
        {
            throw WorkspaceException.Invalid("slug_empty", "The title gives no usable slug; set one.");
        }

        if (await db.Hypotheses.AnyAsync(h => h.Slug == slug, ct).ConfigureAwait(false))
        {
            throw WorkspaceException.Conflict("slug_taken", $"A hypothesis with slug '{slug}' exists.");
        }

        var now = clock.GetUtcNow();
        var hypothesis = new Hypothesis
        {
            Slug = slug,
            Title = input.Title.Trim(),
            Question = input.Question.Trim(),
            Rationale = input.Rationale.Trim(),
            Countries = Countries(input.Countries),
            ConfirmWhen = Lines(input.ConfirmWhen),
            RefuteWhen = Lines(input.RefuteWhen),
            HandlingNote = Trim(input.HandlingNote),
            CreatedBy = by,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Hypotheses.Add(hypothesis);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(slug, ct).ConfigureAwait(false))!;
    }

    public async Task<HypothesisView> UpdateAsync(string slug, HypothesisInput input, CancellationToken ct)
    {
        Validate(input);
        var hypothesis = await db.Hypotheses.FirstOrDefaultAsync(h => h.Slug == slug, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The hypothesis");

        hypothesis.Title = input.Title.Trim();
        hypothesis.Question = input.Question.Trim();
        hypothesis.Rationale = input.Rationale.Trim();
        hypothesis.Countries = Countries(input.Countries);
        hypothesis.ConfirmWhen = Lines(input.ConfirmWhen);
        hypothesis.RefuteWhen = Lines(input.RefuteWhen);
        hypothesis.HandlingNote = Trim(input.HandlingNote);
        hypothesis.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(slug, ct).ConfigureAwait(false))!;
    }

    /// <summary>An indicator is a quote with a URL, as evidence is; the page is kept when it can be.</summary>
    public async Task<HypothesisView> AddIndicatorAsync(string slug, IndicatorInput input, string by, CancellationToken ct)
    {
        var hypothesis = await db.Hypotheses.FirstOrDefaultAsync(h => h.Slug == slug, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The hypothesis");

        if (string.IsNullOrWhiteSpace(input.Statement))
        {
            throw WorkspaceException.Invalid("statement_required", "An indicator says what is documented; write it.");
        }

        if (input.Evidence is null || string.IsNullOrWhiteSpace(input.Evidence.Quote))
        {
            throw WorkspaceException.Invalid("quote_required", "An indicator is a verbatim quote from the source; paste it.");
        }

        if (!Uri.TryCreate(input.Evidence.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw WorkspaceException.Invalid("url_invalid", "An indicator needs an http(s) URL.");
        }

        var now = clock.GetUtcNow();
        var indicator = new Indicator
        {
            HypothesisId = hypothesis.Id,
            Statement = input.Statement.Trim(),
            Level = input.Level,
            ObservedOn = input.ObservedOn,
            Url = uri.ToString(),
            ArchiveUrl = Trim(input.Evidence.ArchiveUrl),
            Publisher = Trim(input.Evidence.Publisher) ?? uri.Host,
            Title = Trim(input.Evidence.Title),
            Quote = Clip(input.Evidence.Quote.Trim(), 4000),
            PublishedOn = input.Evidence.PublishedOn,
            Language = Trim(input.Evidence.Language),
            RetrievedAt = now,
            AddedBy = by,
        };

        if (await capture.TryAsync(uri, "indicator", ct).ConfigureAwait(false) is { } page)
        {
            indicator.SnapshotPath = page.Path;
            indicator.Sha256 = page.Sha256;
            indicator.Title ??= page.Title;
        }

        db.Indicators.Add(indicator);
        hypothesis.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(slug, ct).ConfigureAwait(false))!;
    }

    public async Task<HypothesisView> RemoveIndicatorAsync(string slug, long indicatorId, CancellationToken ct)
    {
        var hypothesis = await db.Hypotheses.Include(h => h.Indicators).FirstOrDefaultAsync(h => h.Slug == slug, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The hypothesis");
        var indicator = hypothesis.Indicators.FirstOrDefault(i => i.Id == indicatorId)
            ?? throw WorkspaceException.NotFound("The indicator");

        db.Indicators.Remove(indicator);
        hypothesis.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(slug, ct).ConfigureAwait(false))!;
    }

    /// <summary>The related actors as a whole list: existing actors only, a hypothesis never creates one.</summary>
    public async Task<HypothesisView> SetActorsAsync(string slug, HypothesisActorsInput input, string by, CancellationToken ct)
    {
        var hypothesis = await db.Hypotheses.Include(h => h.Actors).FirstOrDefaultAsync(h => h.Slug == slug, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The hypothesis");

        var wanted = (input.ActorIds ?? []).Distinct().ToList();
        var known = await db.Actors.Where(a => wanted.Contains(a.Id)).Select(a => a.Id).ToListAsync(ct).ConfigureAwait(false);
        if (known.Count != wanted.Count)
        {
            throw WorkspaceException.Invalid("actor_unknown", "An actor in the list does not exist.");
        }

        var now = clock.GetUtcNow();
        hypothesis.Actors.RemoveAll(a => !wanted.Contains(a.ActorId));
        foreach (var id in wanted.Where(id => hypothesis.Actors.All(a => a.ActorId != id)))
        {
            hypothesis.Actors.Add(new HypothesisActor { HypothesisId = hypothesis.Id, ActorId = id, AddedBy = by, AddedAt = now });
        }

        hypothesis.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(slug, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// Open, Promoted or Dismissed. Promoting needs the slug of a case that exists: the
    /// case is written first, with its actors and claims, and the hypothesis then points
    /// at it. Dismissing needs a note. Every change is appended to the audit table.
    /// </summary>
    public async Task<HypothesisView> DecideAsync(string slug, HypothesisDecisionInput input, string by, CancellationToken ct)
    {
        var hypothesis = await db.Hypotheses.FirstOrDefaultAsync(h => h.Slug == slug, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The hypothesis");

        string? promotedTo = null;
        if (input.Decision == HypothesisStatus.Promoted)
        {
            promotedTo = Trim(input.PromotedToSlug)
                ?? throw WorkspaceException.Invalid("case_required", "Promoting a hypothesis names the case written from it.");
            if (!await db.Campaigns.AnyAsync(c => c.Slug == promotedTo, ct).ConfigureAwait(false))
            {
                throw WorkspaceException.Invalid("case_unknown", $"No case with slug '{promotedTo}'. Write the case first.");
            }
        }

        if (input.Decision == HypothesisStatus.Dismissed && string.IsNullOrWhiteSpace(input.Note))
        {
            throw WorkspaceException.Invalid("note_required", "Dismissing a hypothesis needs a note saying why.");
        }

        var now = clock.GetUtcNow();
        var note = Trim(input.Note);
        db.HypothesisDecisions.Add(new HypothesisDecision
        {
            HypothesisId = hypothesis.Id,
            PreviousStatus = hypothesis.Status,
            Decision = input.Decision,
            Note = note,
            PromotedToSlug = promotedTo,
            DecidedBy = by,
            DecidedAt = now,
        });

        var reopened = input.Decision == HypothesisStatus.Open;
        hypothesis.Status = input.Decision;
        hypothesis.PromotedToSlug = promotedTo;
        hypothesis.DecisionNote = note;
        hypothesis.DecidedBy = reopened ? null : by;
        hypothesis.DecidedAt = reopened ? null : now;
        hypothesis.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(slug, ct).ConfigureAwait(false))!;
    }

    private IQueryable<Hypothesis> Loaded() => db.Hypotheses
        .Include(h => h.Indicators)
        .Include(h => h.Actors).ThenInclude(a => a.Actor);

    private static void Validate(HypothesisInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw WorkspaceException.Invalid("title_required", "A hypothesis needs a title.");
        }

        if (string.IsNullOrWhiteSpace(input.Question))
        {
            throw WorkspaceException.Invalid("question_required", "A hypothesis is a question; write it as one sentence.");
        }

        if (string.IsNullOrWhiteSpace(input.Rationale))
        {
            throw WorkspaceException.Invalid("rationale_required", "Say why the question is worth asking and what is not claimed.");
        }
    }

    private static List<string> Countries(IReadOnlyList<string>? countries) =>
        (countries ?? []).Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length == 2).Distinct().Order().ToList();

    private static List<string> Lines(IReadOnlyList<string>? lines) =>
        (lines ?? []).Select(l => l.Trim()).Where(l => l.Length > 0).ToList();

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string Clip(string value, int max) => value.Length <= max ? value : value[..max];
}
