using System.Text.RegularExpressions;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Workspace;
using Microsoft.EntityFrameworkCore;

namespace HybridMeld.Core.Sources;

/// <summary>Watched sources and the inbox of what they brought. Any user with the grant adds a source; the row says who.</summary>
public sealed partial class SourceService(HybridMeldDbContext db, TimeProvider clock)
{
    public async Task<IReadOnlyList<SourceView>> ListAsync(CancellationToken ct)
    {
        var sources = await db.Sources.AsNoTracking().OrderBy(s => s.Code).ToListAsync(ct).ConfigureAwait(false);
        var lastRuns = await db.SourceRuns.AsNoTracking()
            .GroupBy(r => r.SourceId)
            .Select(g => g.OrderByDescending(r => r.StartedAt).First())
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var newCounts = await db.SourceItems.AsNoTracking()
            .Where(i => i.Status == SourceItemStatus.New)
            .GroupBy(i => i.SourceId)
            .Select(g => new { SourceId = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.SourceId, x => x.Count, ct)
            .ConfigureAwait(false);

        return sources.Select(s =>
        {
            var run = lastRuns.FirstOrDefault(r => r.SourceId == s.Id);
            return ToView(s, run, newCounts.GetValueOrDefault(s.Id));
        }).ToList();
    }

    public async Task<SourceView> CreateAsync(SourceInput input, string by, CancellationToken ct)
    {
        Validate(input);
        var code = string.IsNullOrWhiteSpace(input.Code) ? CodeFrom(input) : input.Code.Trim().ToUpperInvariant();
        if (!CodeShape().IsMatch(code))
        {
            throw WorkspaceException.Invalid("code_shape", "A source code is upper-case letters, digits and underscores, 2 to 60 long.");
        }

        if (await db.Sources.AnyAsync(s => s.Code == code, ct).ConfigureAwait(false))
        {
            throw WorkspaceException.Conflict("code_taken", $"A source with the code {code} exists.");
        }

        var source = new Source
        {
            Id = Guid.CreateVersion7(),
            Code = code,
            Name = input.Name.Trim(),
            Publisher = Trim(input.Publisher),
            Url = input.Url.Trim(),
            Kind = input.Kind,
            FetchKind = input.FetchKind,
            IsOfficial = input.IsOfficial,
            Licence = Trim(input.Licence),
            RefreshHours = input.RefreshHours ?? (input.FetchKind == FetchKind.Telegram ? 1 : 24),
            IsEnabled = input.IsEnabled ?? true,
            AddedBy = by,
            CreatedAt = clock.GetUtcNow(),
        };

        db.Sources.Add(source);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return ToView(source, null, 0);
    }

    public async Task<SourceView> UpdateAsync(string code, SourceInput input, CancellationToken ct)
    {
        Validate(input);
        var source = await db.Sources.FirstOrDefaultAsync(s => s.Code == code, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The source");

        source.Name = input.Name.Trim();
        source.Publisher = Trim(input.Publisher);
        source.Url = input.Url.Trim();
        source.Kind = input.Kind;
        source.FetchKind = input.FetchKind;
        source.IsOfficial = input.IsOfficial;
        source.Licence = Trim(input.Licence);
        source.RefreshHours = input.RefreshHours ?? source.RefreshHours;
        source.IsEnabled = input.IsEnabled ?? source.IsEnabled;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var run = await db.SourceRuns.AsNoTracking().Where(r => r.SourceId == source.Id).OrderByDescending(r => r.StartedAt).FirstOrDefaultAsync(ct).ConfigureAwait(false);
        var fresh = await db.SourceItems.CountAsync(i => i.SourceId == source.Id && i.Status == SourceItemStatus.New, ct).ConfigureAwait(false);
        return ToView(source, run, fresh);
    }

    public async Task<Page<SourceItemView>> InboxAsync(SourceItemStatus? status, string? sourceCode, string? q, int limit, int offset, CancellationToken ct)
    {
        var query = db.SourceItems.AsNoTracking().Include(i => i.Source).AsQueryable();
        if (status is { } s)
        {
            query = query.Where(i => i.Status == s);
        }

        if (!string.IsNullOrWhiteSpace(sourceCode))
        {
            var code = sourceCode.Trim().ToUpperInvariant();
            query = query.Where(i => i.Source!.Code == code);
        }

        if (!string.IsNullOrWhiteSpace(q))
        {
            var pattern = $"%{q.Trim()}%";
            query = query.Where(i => EF.Functions.ILike(i.Title ?? "", pattern) || EF.Functions.ILike(i.Text ?? "", pattern));
        }

        var total = await query.CountAsync(ct).ConfigureAwait(false);
        var items = await query
            .OrderByDescending(i => i.PublishedAt ?? i.FetchedAt)
            .Skip(offset)
            .Take(Math.Clamp(limit, 1, 200))
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var ids = items.Select(i => i.Id).ToList();
        var linked = await db.Claims.AsNoTracking()
            .Where(c => c.SourceItemId != null && ids.Contains(c.SourceItemId.Value))
            .GroupBy(c => c.SourceItemId!.Value)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToDictionaryAsync(x => x.Id, x => x.Count, ct)
            .ConfigureAwait(false);

        return new Page<SourceItemView>(items.Select(i => ToView(i, linked.GetValueOrDefault(i.Id))).ToList(), total);
    }

    public async Task<SourceItemView?> GetItemAsync(Guid id, CancellationToken ct)
    {
        var item = await db.SourceItems.AsNoTracking().Include(i => i.Source).FirstOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false);
        if (item is null)
        {
            return null;
        }

        var linked = await db.Claims.CountAsync(c => c.SourceItemId == id, ct).ConfigureAwait(false);
        return ToView(item, linked);
    }

    public async Task<SourceItemView> TriageAsync(Guid id, TriageInput input, string by, CancellationToken ct)
    {
        var item = await db.SourceItems.Include(i => i.Source).FirstOrDefaultAsync(i => i.Id == id, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The inbox item");

        if (input.Status == SourceItemStatus.Linked)
        {
            throw WorkspaceException.Invalid("linked_by_claim", "An item becomes Linked by making a claim from it, not by hand.");
        }

        item.Status = input.Status;
        item.TriagedBy = by;
        item.TriagedAt = clock.GetUtcNow();
        item.TriageNote = Trim(input.Note);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var linked = await db.Claims.CountAsync(c => c.SourceItemId == id, ct).ConfigureAwait(false);
        return ToView(item, linked);
    }

    private static SourceView ToView(Source s, SourceRun? run, int fresh) => new(
        s.Id, s.Code, s.Name, s.Publisher, s.Url, s.Kind, s.FetchKind, s.IsOfficial, s.Licence, s.RefreshHours, s.IsEnabled,
        s.AddedBy, s.CreatedAt, run?.StartedAt, run?.Status, run?.Error, fresh);

    private static SourceItemView ToView(SourceItem i, int linked) => new(
        i.Id, i.SourceId, i.Source!.Code, i.Source.Name, i.ExternalId, i.Url, i.Title, i.Text, i.PublishedAt, i.FetchedAt,
        i.Status, i.TriagedBy, i.TriagedAt, i.TriageNote, linked);

    private static void Validate(SourceInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name))
        {
            throw WorkspaceException.Invalid("name_required", "A source needs a name.");
        }

        if (!Uri.TryCreate(input.Url?.Trim(), UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
        {
            throw WorkspaceException.Invalid("url_invalid", "A source needs an http(s) URL.");
        }

        if (input.RefreshHours is < 1 or > 24 * 365)
        {
            throw WorkspaceException.Invalid("refresh_range", "Refresh is between 1 hour and a year.");
        }
    }

    /// <summary>"TG_SOMECHANNEL" for a Telegram channel, otherwise the host: "NUKIB_GOV_CZ".</summary>
    private static string CodeFrom(SourceInput input)
    {
        var stem = input.FetchKind == FetchKind.Telegram
            ? "TG_" + TelegramFetcher.ChannelName(input.Url)
            : new Uri(input.Url.Trim()).Host.Replace("www.", string.Empty, StringComparison.OrdinalIgnoreCase);
        var code = NotCode().Replace(stem.ToUpperInvariant(), "_").Trim('_');
        return code.Length > 60 ? code[..60] : code;
    }

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    [GeneratedRegex("^[A-Z0-9_]{2,60}$")]
    private static partial Regex CodeShape();

    [GeneratedRegex("[^A-Z0-9]+")]
    private static partial Regex NotCode();
}
