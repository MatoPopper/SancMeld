using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Links;
using Microsoft.EntityFrameworkCore;
using SancMeld.Core.Matching;

namespace HybridMeld.Core.Workspace;

public sealed class ActorService(HybridMeldDbContext db, LinkResolver links, TimeProvider clock)
{
    /// <summary>
    /// Name search over actors and their aliases, in SancMeld's normal form: "sandworm"
    /// reaches GRU unit 74455 through the MITRE alias. Containment, not trigrams: a few
    /// hundred actors need no index strategy, and a substring is what an analyst types.
    /// </summary>
    public async Task<IReadOnlyList<ActorHit>> SearchAsync(string? q, ActorKind? kind, int limit, CancellationToken ct)
    {
        var take = Math.Clamp(limit, 1, 200);
        var normalized = NameNormalizer.Normalize(q);
        IQueryable<Actor> query = db.Actors.AsNoTracking().Include(a => a.Aliases);

        if (kind is { } k)
        {
            query = query.Where(a => a.Kind == k);
        }

        if (normalized.Length > 0)
        {
            var pattern = $"%{normalized}%";
            query = query.Where(a => EF.Functions.ILike(a.NameNormalized, pattern) || a.Aliases.Any(x => EF.Functions.ILike(x.NameNormalized, pattern)));
        }

        var actors = await query.OrderBy(a => a.Name).Take(take).ToListAsync(ct).ConfigureAwait(false);
        var ids = actors.Select(a => a.Id).ToList();
        var counts = await db.Claims.AsNoTracking()
            .Where(c => c.Status != ClaimStatus.Withdrawn && (ids.Contains(c.SubjectActorId) || (c.ObjectActorId != null && ids.Contains(c.ObjectActorId.Value))))
            .Select(c => new { c.SubjectActorId, c.ObjectActorId })
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return actors.Select(a =>
        {
            var matchedAlias = normalized.Length > 0 && !a.NameNormalized.Contains(normalized, StringComparison.Ordinal)
                ? a.Aliases.FirstOrDefault(x => x.NameNormalized.Contains(normalized, StringComparison.Ordinal))?.Name
                : null;
            var count = counts.Count(c => c.SubjectActorId == a.Id || c.ObjectActorId == a.Id);
            return new ActorHit(Mapping.ToRef(a), a.Description, matchedAlias, count);
        }).ToList();
    }

    public async Task<ActorView?> GetAsync(Guid id, CancellationToken ct)
    {
        var actor = await db.Actors.AsNoTracking().Include(a => a.Aliases).FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false);
        if (actor is null)
        {
            return null;
        }

        var claims = await db.Claims.AsNoTracking()
            .Include(c => c.Campaign)
            .Include(c => c.Subject)
            .Include(c => c.Object)
            .Include(c => c.Evidence)
            .Where(c => c.SubjectActorId == id || c.ObjectActorId == id)
            .OrderByDescending(c => c.UpdatedAt)
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        var campaignIds = claims.Select(c => c.CampaignId).Distinct().ToList();
        var campaigns = await db.Campaigns.AsNoTracking()
            .Include(c => c.AttributedTo)
            .Include(c => c.Claims).ThenInclude(x => x.Subject)
            .Include(c => c.Claims).ThenInclude(x => x.Object)
            .Where(c => campaignIds.Contains(c.Id))
            .AsSplitQuery()
            .ToListAsync(ct)
            .ConfigureAwait(false);

        return new ActorView(
            Mapping.ToRef(actor), actor.Description, Mapping.ParseJson(actor.IdentifiersJson), actor.PublicRoleJustification,
            actor.SanctionCachedName, actor.PepCachedName, actor.LinkVerifiedAt, actor.LinkStaleAt,
            actor.Aliases.OrderBy(a => a.Name).Select(Mapping.ToView).ToList(),
            campaigns.OrderByDescending(c => c.UpdatedAt).Select(Mapping.ToSummary).ToList(),
            claims.Select(Mapping.ToView).ToList(),
            actor.CreatedBy, actor.CreatedAt);
    }

    public async Task<ActorView> CreateAsync(ActorInput input, string by, CancellationToken ct)
    {
        Validate(input);
        var now = clock.GetUtcNow();
        var actor = new Actor
        {
            Id = Guid.CreateVersion7(),
            Kind = input.Kind,
            Name = input.Name.Trim(),
            NameNormalized = NameNormalizer.Normalize(input.Name),
            Country = Country(input.Country),
            IdentifiersJson = input.Identifiers?.GetRawText(),
            Description = Trim(input.Description),
            IsPublicFigure = input.IsPublicFigure,
            PublicRoleJustification = Trim(input.PublicRoleJustification),
            CreatedBy = by,
            CreatedAt = now,
            UpdatedAt = now,
        };

        AddAliases(actor, input.Aliases, now);
        db.Actors.Add(actor);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(actor.Id, ct).ConfigureAwait(false))!;
    }

    public async Task<ActorView> UpdateAsync(Guid id, ActorInput input, CancellationToken ct)
    {
        Validate(input);
        var actor = await db.Actors.Include(a => a.Aliases).FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The actor");

        var now = clock.GetUtcNow();
        actor.Kind = input.Kind;
        actor.Name = input.Name.Trim();
        actor.NameNormalized = NameNormalizer.Normalize(input.Name);
        actor.Country = Country(input.Country);
        actor.IdentifiersJson = input.Identifiers?.GetRawText();
        actor.Description = Trim(input.Description);
        actor.IsPublicFigure = input.IsPublicFigure;
        actor.PublicRoleJustification = Trim(input.PublicRoleJustification);
        actor.UpdatedAt = now;

        // Aliases an analyst typed are replaced by what they typed now; ones a sync brought
        // in (misp, mitre, sanctions) are the sync's to manage and stay.
        if (input.Aliases is not null)
        {
            actor.Aliases.RemoveAll(a => a.Origin == "analyst");
            AddAliases(actor, input.Aliases, now);
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(actor.Id, ct).ConfigureAwait(false))!;
    }

    /// <summary>Links the actor to a sanctions record and/or a PEP person, by id. The target is looked up now when its database is configured; the nightly job keeps it checked.</summary>
    public async Task<ActorView> SetLinksAsync(Guid id, ActorLinkInput input, CancellationToken ct)
    {
        var actor = await db.Actors.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The actor");

        var now = clock.GetUtcNow();
        actor.SanctionDesignationId = input.SanctionDesignationId;
        actor.PepPersonId = input.PepPersonId;
        actor.SanctionSourceCode = null;
        actor.SanctionCachedName = null;
        actor.PepSourceCode = null;
        actor.PepCachedName = null;
        actor.LinkStaleAt = null;
        actor.LinkVerifiedAt = null;

        if (input.SanctionDesignationId is { } designationId)
        {
            var target = await links.FindDesignationAsync(designationId, ct).ConfigureAwait(false);
            if (target is null && links.SanctionsConfigured)
            {
                throw WorkspaceException.Invalid("designation_unknown", "No sanctions record has that id.");
            }

            actor.SanctionSourceCode = target?.SourceCode;
            actor.SanctionCachedName = target?.Name;
            actor.LinkVerifiedAt = target is null ? null : now;
            actor.LinkStaleAt = target is { IsCurrent: false } ? now : null;
        }

        if (input.PepPersonId is { } personId)
        {
            var target = await links.FindPepPersonAsync(personId, ct).ConfigureAwait(false);
            if (target is null && links.PepConfigured)
            {
                throw WorkspaceException.Invalid("pep_unknown", "No PEP person has that id.");
            }

            actor.PepSourceCode = target?.SourceCode;
            actor.PepCachedName = target?.Name;
            actor.LinkVerifiedAt ??= target is null ? null : now;
        }

        actor.UpdatedAt = now;
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return (await GetAsync(actor.Id, ct).ConfigureAwait(false))!;
    }

    /// <summary>
    /// The one deletion in the workspace: a person asks to be removed. The actor and
    /// every claim naming them go; the decisions stay without a name, so the audit
    /// trail still says a claim existed and was decided.
    /// </summary>
    public async Task<int> ForgetAsync(Guid id, CancellationToken ct)
    {
        var actor = await db.Actors.FirstOrDefaultAsync(a => a.Id == id, ct).ConfigureAwait(false)
            ?? throw WorkspaceException.NotFound("The actor");

        var claims = await db.Claims.Where(c => c.SubjectActorId == id || c.ObjectActorId == id).ToListAsync(ct).ConfigureAwait(false);
        db.Claims.RemoveRange(claims);
        await db.Campaigns.Where(c => c.AttributedToActorId == id).ExecuteUpdateAsync(s => s.SetProperty(c => c.AttributedToActorId, (Guid?)null), ct).ConfigureAwait(false);
        db.Actors.Remove(actor);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        return claims.Count;
    }

    private static void AddAliases(Actor actor, IReadOnlyList<string>? aliases, DateTimeOffset now)
    {
        foreach (var alias in (aliases ?? []).Select(a => a.Trim()).Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var normalized = NameNormalizer.Normalize(alias);
            if (normalized.Length == 0 || normalized == actor.NameNormalized || actor.Aliases.Any(a => a.NameNormalized == normalized))
            {
                continue;
            }

            actor.Aliases.Add(new ActorAlias { ActorId = actor.Id, Name = alias, NameNormalized = normalized, Origin = "analyst", CreatedAt = now });
        }
    }

    private static void Validate(ActorInput input)
    {
        if (string.IsNullOrWhiteSpace(input.Name) || NameNormalizer.Normalize(input.Name).Length == 0)
        {
            throw WorkspaceException.Invalid("name_required", "An actor needs a name.");
        }

        if (input.Identifiers is { } json && json.ValueKind != System.Text.Json.JsonValueKind.Object)
        {
            throw WorkspaceException.Invalid("identifiers_shape", "Identifiers are an object: {\"domain\": \"…\"}.");
        }
    }

    private static string? Country(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToUpperInvariant()[..Math.Min(2, value.Trim().Length)];

    private static string? Trim(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}
