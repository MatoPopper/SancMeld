using System.Text.Json;
using System.Text.Json.Serialization;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using HybridMeld.Core.Links;
using HybridMeld.Core.Workspace;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using SancMeld.Core.Matching;

namespace HybridMeld.Core.Seed;

/// <summary>
/// A curated case from a file: the way case files written outside the service (the
/// private case studies, an analyst's notes) get in, and the way a case is reproduced on
/// another instance. The file names actors by a key; an actor is matched to an existing
/// one by its exact name or alias in the normal form (so "Voice of Europe" lands on the
/// actor the regime seed made, with its sanctions link), and created otherwise. Claims
/// are matched by case, subject, predicate, object and statement, so a re-import adds
/// what is new and changes nothing else. Claims go through ClaimService: the same rule
/// (no claim without a quote) and the same page snapshot as a claim typed in the screen.
/// </summary>
public sealed class CaseImportService(HybridMeldDbContext db, CampaignService campaigns, ActorService actors, ClaimService claims, LinkResolver links, ILogger<CaseImportService> logger)
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter() },
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    public sealed record CaseFile(CaseCampaign Campaign, IReadOnlyList<CaseActor> Actors, IReadOnlyList<CaseClaim> Claims);

    public sealed record CaseCampaign(
        string Slug, string Title, string Summary, CampaignKind Kind, CampaignStatus Status, IReadOnlyList<string>? Countries,
        DateOnly? StartedOn, DateOnly? EndedOn, string? AttributedTo, EditorialStatus Editorial = EditorialStatus.Draft);

    public sealed record CaseActor(
        string Key, string Name, ActorKind Kind, string? Country, string? Description, bool IsPublicFigure,
        string? PublicRoleJustification, IReadOnlyList<string>? Aliases, JsonElement? Identifiers,
        // The person's name as the PEP lists write it; resolved to a PepMeld id on this instance, since ids differ between instances.
        string? PepPersonName = null);

    public sealed record CaseClaim(
        string Subject, Predicate Predicate, string? Object, string Statement, EvidenceLevel Level,
        ClaimStatus Status, DateOnly? OccurredFrom, DateOnly? OccurredTo, IReadOnlyList<EvidenceInput> Evidence);

    public sealed record ImportOutcome(string Slug, bool CampaignCreated, int ActorsMatched, int ActorsCreated, int ClaimsCreated, int ClaimsExisting, IReadOnlyList<string> Notes);

    public async Task<ImportOutcome> ImportAsync(string json, string by, CancellationToken ct)
    {
        var file = JsonSerializer.Deserialize<CaseFile>(json, Json) ?? throw new InvalidDataException("Empty case file.");
        var notes = new List<string>();

        // 1. actors, by key
        var byKey = new Dictionary<string, Guid>(StringComparer.Ordinal);
        int matched = 0, created = 0;
        foreach (var a in file.Actors)
        {
            var existing = await FindActorAsync(a, ct).ConfigureAwait(false);
            if (existing is not null)
            {
                byKey[a.Key] = existing.Id;
                matched++;
                if (!string.IsNullOrWhiteSpace(a.Description) && string.IsNullOrWhiteSpace(existing.Description))
                {
                    existing.Description = a.Description.Trim();
                    await db.SaveChangesAsync(ct).ConfigureAwait(false);
                }

                notes.Add($"actor {a.Key} = existing {existing.Name}" + (existing.SanctionDesignationId is not null ? $" (sanctions {existing.SanctionSourceCode})" : string.Empty));
                if (existing.PepPersonId is null)
                {
                    await LinkPepAsync(a, existing.Id, notes, ct).ConfigureAwait(false);
                }

                continue;
            }

            var view = await actors.CreateAsync(new ActorInput(a.Kind, a.Name, a.Country, a.Identifiers, a.Description, a.IsPublicFigure, a.PublicRoleJustification, a.Aliases), by, ct).ConfigureAwait(false);
            byKey[a.Key] = view.Actor.Id;
            created++;
            notes.Add($"actor {a.Key} created: {a.Name}");
            await LinkPepAsync(a, view.Actor.Id, notes, ct).ConfigureAwait(false);
        }

        // 2. campaign, by slug
        var c = file.Campaign;
        var campaignCreated = false;
        Guid? attributed = c.AttributedTo is { } key ? byKey.GetValueOrDefault(key) : null;
        var campaign = await db.Campaigns.FirstOrDefaultAsync(x => x.Slug == c.Slug, ct).ConfigureAwait(false);
        if (campaign is null)
        {
            var summary = await campaigns.CreateAsync(new CampaignInput(c.Title, c.Summary, c.Kind, c.Status, c.Countries, c.StartedOn, c.EndedOn, attributed, c.Slug), by, ct).ConfigureAwait(false);
            campaign = await db.Campaigns.FirstAsync(x => x.Id == summary.Id, ct).ConfigureAwait(false);
            campaignCreated = true;
        }
        else
        {
            await campaigns.UpdateAsync(c.Slug, new CampaignInput(c.Title, c.Summary, c.Kind, c.Status, c.Countries, c.StartedOn, c.EndedOn, attributed, null), ct).ConfigureAwait(false);
        }

        // 3. claims, by (case, subject, predicate, object, statement)
        int claimsCreated = 0, claimsExisting = 0;
        foreach (var cl in file.Claims)
        {
            if (!byKey.TryGetValue(cl.Subject, out var subjectId))
            {
                throw new InvalidDataException($"Claim refers to unknown actor key '{cl.Subject}'.");
            }

            Guid? objectId = null;
            if (cl.Object is { } objectKey)
            {
                if (!byKey.TryGetValue(objectKey, out var o))
                {
                    throw new InvalidDataException($"Claim refers to unknown actor key '{objectKey}'.");
                }

                objectId = o;
            }

            var statement = cl.Statement.Trim();
            var exists = await db.Claims.AnyAsync(x => x.CampaignId == campaign.Id && x.SubjectActorId == subjectId && x.Predicate == cl.Predicate && x.ObjectActorId == objectId && x.Statement == statement, ct).ConfigureAwait(false);
            if (exists)
            {
                claimsExisting++;
                continue;
            }

            var view = await claims.CreateAsync(new ClaimInput(campaign.Id, subjectId, cl.Predicate, objectId, statement, cl.Level, cl.OccurredFrom, cl.OccurredTo, null, cl.Evidence), by, FoundBy.Analyst, ct).ConfigureAwait(false);
            if (cl.Status != ClaimStatus.Candidate)
            {
                await claims.DecideAsync(view.Id, new ClaimDecisionInput(cl.Status, "Imported from a case file."), by, ct).ConfigureAwait(false);
            }

            foreach (var e in view.Evidence.Where(e => !e.HasSnapshot))
            {
                notes.Add($"claim {view.Id}: no snapshot of {e.Url} (page not fetched; the quote stands on its own)");
            }

            claimsCreated++;
        }

        // 4. editorial state last, so a published case is complete when it appears
        if (c.Editorial != EditorialStatus.Draft)
        {
            await campaigns.SetEditorialAsync(c.Slug, new EditorialInput(c.Editorial, c.Editorial == EditorialStatus.Withdrawn ? "Imported as withdrawn." : null), ct).ConfigureAwait(false);
        }

        logger.LogInformation("Case {Slug}: campaign {Created}, actors {Matched} matched / {New} new, claims {Claims} new / {Existing} existing.", c.Slug, campaignCreated ? "created" : "updated", matched, created, claimsCreated, claimsExisting);
        return new ImportOutcome(c.Slug, campaignCreated, matched, created, claimsCreated, claimsExisting, notes);
    }

    /// <summary>The PEP link by name, when the file names one and this instance has the PEP database: the overlap a case is here for.</summary>
    private async Task LinkPepAsync(CaseActor a, Guid actorId, List<string> notes, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(a.PepPersonName))
        {
            return;
        }

        if (!links.PepConfigured)
        {
            notes.Add($"actor {a.Key}: PEP database not configured, link to '{a.PepPersonName}' not set");
            return;
        }

        var found = await links.FindPepPersonByNameAsync(NameNormalizer.Normalize(a.PepPersonName), ct).ConfigureAwait(false);
        if (found is null)
        {
            notes.Add($"actor {a.Key}: no PEP person named '{a.PepPersonName}'");
            return;
        }

        var actor = await db.Actors.FirstAsync(x => x.Id == actorId, ct).ConfigureAwait(false);
        await actors.SetLinksAsync(actorId, new ActorLinkInput(actor.SanctionDesignationId, found.Value.Id), ct).ConfigureAwait(false);
        notes.Add($"actor {a.Key}: PEP {found.Value.Target.SourceCode} {found.Value.Target.Name}");
    }

    private async Task<Actor?> FindActorAsync(CaseActor a, CancellationToken ct)
    {
        var names = new[] { a.Name }.Concat(a.Aliases ?? []).Select(NameNormalizer.Normalize).Where(n => n.Length > 0).Distinct().ToList();
        return await db.Actors
            .Include(x => x.Aliases)
            .Where(x => names.Contains(x.NameNormalized) || x.Aliases.Any(al => names.Contains(al.NameNormalized)))
            // A sanctioned actor first: that is the one with the link worth keeping.
            .OrderByDescending(x => x.SanctionDesignationId != null)
            .ThenBy(x => x.CreatedAt)
            .FirstOrDefaultAsync(ct)
            .ConfigureAwait(false);
    }
}
