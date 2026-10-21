using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Workspace;

/// <summary>
/// Cases. A reader sees Published ones; whoever has the grant sees Drafts too, since in
/// the first version every user with the grant is a curator (user's decision,
/// 2026-10-21: one flag, no editor role).
/// </summary>
public sealed partial class CampaignService(HybridMeldDbContext db, TimeProvider clock)
{
    public async Task<Page<CampaignSummary>> ListAsync(
        CampaignKind? kind, CampaignStatus? status, string? country, EditorialStatus? editorial, string? q, int limit, int offset, CancellationToken ct)
    {
        var query = Loaded().AsNoTracking();

        if (kind is { } k)
        {
            query = query.Where(c => c.Kind == k);
        }

        if (status is { } s)
        {
            query = query.Where(c => c.Status == s);
        }

        if (editorial is { } e)
        {
            query = query.Where(c => c.Editorial == e);
        }

        if (!string.IsNullOrWhiteSpace(country))
        {
            var iso = country.Trim().ToUpperInvariant();
            query = query.Where(c => c.Countries.Contains(iso));
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(c => EF.Functions.ILike(c.Title, pattern) || EF.Functions.ILike(c.Summary, pattern));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(c => c.UpdatedAt)
            .Skip(offset)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new Page<CampaignSummary>(items.Select(Mapping.ToSummary).ToList(), total);
    }

    public async Task<CampaignView?> GetAsync(string slug, CancellationToken ct)
    {
        var campaign = await Loaded()
            .Include(c => c.Claims).ThenInclude(x => x.Evidence)
            .AsNoTracking()
            .AsSplitQuery()
            .FirstOrDefaultAsync(c => c.Slug == slug, ct)
            .ConfigureAwait(false);

        if (campaign is null)
        {
            return null;
        }

        var claims = campaign.Claims
            .OrderByDescending(x => x.Status == ClaimStatus.Confirmed)
            .ThenBy(x => x.Level)
            .ThenByDescending(x => x.UpdatedAt)
            .Select(Mapping.ToView)
            .ToList();

        var actors = campaign.Claims
            .Where(x => x.Status != ClaimStatus.Withdrawn)
            .SelectMany(x => new[] { x.Subject, x.Object })
            .Where(a => a is not null)
            .Select(a => a!)
            .DistinctBy(a => a.Id)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(Mapping.ToRef)
            .ToList();

        return new CampaignView(Mapping.ToSummary(campaign), actors, claims);
    }

    public async Task<CampaignSummary> CreateAsync(CampaignInput input, string by, CancellationToken ct)
    {
        Validate(input);
        var slug = Slugify(string.IsNullOrWhiteSpace(input.Slug) ? input.Title : input.Slug);
        if (slug.Length == 0)
        {
            throw WorkspaceException.Invalid("slug_empty", "The title gives no usable slug; set one.");
        }

        if (await db.Campaigns.AnyAsync(c => c.Slug == slug, ct).ConfigureAwait(false))
        {
            throw WorkspaceException.Conflict("slug_taken", $"A case with the slug '{slug}' exists.");
        }

        await EnsureActorAsync(input.AttributedToActorId, ct).ConfigureAwait(false);
        var now = clock.GetUtcNow();
        var campaign = new Campaign
        {
            Id = Guid.CreateVersion7(),
            Slug = slug,
            Title = input.Title.Trim(),
            Summary = input.Summary.Trim(),
            Kind = input.Kind,
            Status = input.Status,
            Countries = Countries(input.Countries),
            StartedOn = input.StartedOn,
            EndedOn = input.EndedOn,
            AttributedToActorId = input.AttributedToActorId,
            CreatedBy = by,
            CreatedAt = now,
            UpdatedAt = now,
        };

        db.Campaigns.Add(campaign);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Mapping.ToSummary(await Loaded().FirstAsync(c => c.Id == campaign.Id, ct).ConfigureAwait(false));
    }

    public async Task<CampaignSummary> UpdateAsync(string slug, CampaignInput input, CancellationToken ct)
    {
        Validate(input);
        var campaign = await Loaded().FirstOrDefaultAsync(c => c.Slug == slug, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The case");

        await EnsureActorAsync(input.AttributedToActorId, ct).ConfigureAwait(false);
        campaign.Title = input.Title.Trim();
        campaign.Summary = input.Summary.Trim();
        campaign.Kind = input.Kind;
        campaign.Status = input.Status;
        campaign.Countries = Countries(input.Countries);
        campaign.StartedOn = input.StartedOn;
        campaign.EndedOn = input.EndedOn;
        campaign.AttributedToActorId = input.AttributedToActorId;
        campaign.UpdatedAt = clock.GetUtcNow();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        return Mapping.ToSummary(await Loaded().AsNoTracking().FirstAsync(c => c.Id == campaign.Id, ct).ConfigureAwait(false));
    }

    /// <summary>Draft → Published → Withdrawn, and back to Draft. Withdrawing needs a reason: the case stays visible, greyed, with it.</summary>
    public async Task<CampaignSummary> SetEditorialAsync(string slug, EditorialInput input, CancellationToken ct)
    {
        var campaign = await Loaded().FirstOrDefaultAsync(c => c.Slug == slug, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The case");

        if (input.Editorial == EditorialStatus.Withdrawn && string.IsNullOrWhiteSpace(input.Reason))
        {
            throw WorkspaceException.Invalid("reason_required", "Withdrawing a case needs a reason.");
        }

        var now = clock.GetUtcNow();
        campaign.Editorial = input.Editorial;
        campaign.WithdrawnReason = input.Editorial == EditorialStatus.Withdrawn ? input.Reason!.Trim() : null;
        campaign.PublishedAt = input.Editorial == EditorialStatus.Published ? campaign.PublishedAt ?? now : campaign.PublishedAt;
        campaign.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return Mapping.ToSummary(campaign);
    }

    private IQueryable<Campaign> Loaded() => db.Campaigns
        .Include(c => c.AttributedTo)
        .Include(c => c.Claims).ThenInclude(x => x.Subject)
        .Include(c => c.Claims).ThenInclude(x => x.Object);

    private async Task EnsureActorAsync(Guid? id, CancellationToken ct)
    {
        if (id is { } actorId && !await db.Actors.AnyAsync(a => a.Id == actorId, ct).ConfigureAwait(false))
        {
            throw WorkspaceException.Invalid("actor_unknown", "The attributed actor does not exist.");
        }
    }

    private static void Validate(CampaignInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Title))
        {
            throw WorkspaceException.Invalid("title_required", "A case needs a title.");
        }

        if (string.IsNullOrWhiteSpace(input.Summary))
        {
            throw WorkspaceException.Invalid("summary_required", "A case needs a summary: what happened and who says so.");
        }

        if (input.StartedOn is { } from && input.EndedOn is { } to && to < from)
        {
            throw WorkspaceException.Invalid("dates_reversed", "The case cannot end before it started.");
        }
    }

    private static List<string> Countries(IReadOnlyList<string>? countries) =>
        (countries ?? []).Select(c => c.Trim().ToUpperInvariant()).Where(c => c.Length == 2).Distinct().Order().ToList();

    /// <summary>ASCII, lowercase, hyphens: "Voice of Europe – platby" → "voice-of-europe-platby".</summary>
    public static string Slugify(string text)
    {
        var folded = text.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(folded.Length);
        foreach (var ch in folded)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(ch) != UnicodeCategory.NonSpacingMark)
            {
                sb.Append(ch);
            }
        }

        var ascii = sb.ToString().Normalize(NormalizationForm.FormC).ToLowerInvariant();
        return SlugSeparators().Replace(ascii, "-").Trim('-');
    }

    [GeneratedRegex("[^a-z0-9]+")]
    private static partial Regex SlugSeparators();
}
