using HybridMeld.Core.Configuration;
using HybridMeld.Core.Data;
using HybridMeld.Core.Domain;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using SancMeld.Core.Relations;

namespace HybridMeld.Core.Graph;

/// <summary>
/// The case or the actor as a graph, in the records the sanctions graph uses
/// (SancMeld.Core.Relations.GraphModels), so the front end draws both with one
/// component and one legend. A node is an actor, an edge is a claim; an actor linked to a
/// sanctions record or a PEP person gets a second node of that kind, with a route out to
/// the compliance screen. Nothing points back.
///
/// Colour is status, as there: Confirmed green, Candidate amber, Rejected grey, the
/// subject teal. Review (red) is not used: the workspace has no binding listing to reach.
/// </summary>
public sealed class HybridGraphBuilder(HybridMeldDbContext db, IOptions<HybridMeldOptions> options)
{
    private const int MaxEdges = 400;

    public async Task<DesignationGraph?> ForCampaignAsync(string slug, CancellationToken ct)
    {
        var campaign = await db.Campaigns.AsNoTracking().FirstOrDefaultAsync(c => c.Slug == slug, ct).ConfigureAwait(false);
        if (campaign is null)
        {
            return null;
        }

        var claims = await Claims().Where(c => c.CampaignId == campaign.Id).ToListAsync(ct).ConfigureAwait(false);
        return Build(campaign.Id, subjectActorId: campaign.AttributedToActorId, claims);
    }

    public async Task<DesignationGraph?> ForActorAsync(Guid id, CancellationToken ct)
    {
        if (!await db.Actors.AnyAsync(a => a.Id == id, ct).ConfigureAwait(false))
        {
            return null;
        }

        // The actor's claims, and the claims of everyone one step away, so the neighbour's
        // own ties show; two steps would draw the whole workspace around a hub.
        var own = await Claims().Where(c => c.SubjectActorId == id || c.ObjectActorId == id).ToListAsync(ct).ConfigureAwait(false);
        var neighbours = own.SelectMany(c => new[] { c.SubjectActorId, c.ObjectActorId }).Where(x => x is not null && x != id).Select(x => x!.Value).Distinct().ToList();
        var ownIds = own.Select(c => c.Id).ToList();
        var around = neighbours.Count == 0
            ? []
            : await Claims()
                .Where(c => !ownIds.Contains(c.Id) && (neighbours.Contains(c.SubjectActorId) || (c.ObjectActorId != null && neighbours.Contains(c.ObjectActorId.Value))))
                .OrderByDescending(c => c.Status == ClaimStatus.Confirmed)
                .Take(MaxEdges)
                .ToListAsync(ct)
                .ConfigureAwait(false);

        return Build(id, subjectActorId: id, own.Concat(around).ToList());
    }

    private DesignationGraph Build(Guid graphId, Guid? subjectActorId, List<Claim> claims)
    {
        var live = claims.Where(c => c.Status != ClaimStatus.Withdrawn).ToList();
        var truncated = live.Count > MaxEdges;
        live = live.OrderByDescending(c => c.Status == ClaimStatus.Confirmed).ThenBy(c => c.Level).Take(MaxEdges).ToList();

        var nodes = new Dictionary<string, GraphNode>(StringComparer.Ordinal);
        var edges = new List<GraphEdge>(live.Count);
        var links = options.Value.Links;

        void AddActor(Actor a)
        {
            var id = "a:" + a.Id;
            if (nodes.ContainsKey(id))
            {
                return;
            }

            var touching = live.Where(c => c.SubjectActorId == a.Id || c.ObjectActorId == a.Id).ToList();
            var status = a.Id == subjectActorId ? GraphStatus.Subject
                : touching.Any(c => c.Status == ClaimStatus.Confirmed) ? GraphStatus.Confirmed
                : touching.Any(c => c.Status == ClaimStatus.Candidate) ? GraphStatus.Candidate
                : GraphStatus.Rejected;

            nodes[id] = new GraphNode
            {
                Id = id,
                Kind = GraphNodeKind.Actor,
                Label = a.Name,
                Status = status,
                CountryIso2 = a.Country,
                Detail = ActorDetail(a),
                Route = "/hybrid/actors/" + a.Id,
                DesignationId = a.SanctionDesignationId,
                Source = a.SanctionSourceCode ?? a.PepSourceCode,
            };

            // The compliance side of the actor: a listing node and a PEP node, each with its
            // route out. Status Confirmed: the link was set by hand against an id, and the
            // nightly check greys it (Rejected) once the target is gone.
            if (a.SanctionDesignationId is { } designationId)
            {
                var listingId = "d:" + designationId;
                nodes.TryAdd(listingId, new GraphNode
                {
                    Id = listingId,
                    Kind = GraphNodeKind.Listing,
                    Label = a.SanctionCachedName ?? a.Name,
                    Status = a.LinkStaleAt is null ? GraphStatus.Confirmed : GraphStatus.Rejected,
                    DesignationId = designationId,
                    Source = a.SanctionSourceCode,
                    Detail = a.LinkStaleAt is null ? "sankčný zoznam" : "sankčný zoznam – záznam už nie je aktívny",
                    Route = links.SanctionRoute.Replace("{id}", designationId.ToString(), StringComparison.Ordinal),
                });
                edges.Add(new GraphEdge
                {
                    From = id,
                    To = listingId,
                    Kind = "same_as",
                    Status = a.LinkStaleAt is null ? GraphStatus.Confirmed : GraphStatus.Rejected,
                    Evidence = $"Ten istý subjekt ako záznam {a.SanctionSourceCode ?? "sankčného zoznamu"}.",
                });
            }

            if (a.PepPersonId is { } personId)
            {
                var pepId = "pep:" + personId;
                nodes.TryAdd(pepId, new GraphNode
                {
                    Id = pepId,
                    Kind = GraphNodeKind.Party,
                    Label = a.PepCachedName ?? a.Name,
                    Status = a.LinkStaleAt is null ? GraphStatus.Confirmed : GraphStatus.Rejected,
                    Source = a.PepSourceCode,
                    Detail = "politicky exponovaná osoba (PepMeld)",
                    Route = links.PepRoute.Replace("{name}", Uri.EscapeDataString(a.PepCachedName ?? a.Name), StringComparison.Ordinal),
                });
                edges.Add(new GraphEdge
                {
                    From = id,
                    To = pepId,
                    Kind = "same_as",
                    Status = a.LinkStaleAt is null ? GraphStatus.Confirmed : GraphStatus.Rejected,
                    Evidence = $"Tá istá osoba ako PEP záznam {a.PepSourceCode ?? "PepMeld"}.",
                });
            }
        }

        foreach (var claim in live)
        {
            AddActor(claim.Subject!);
            if (claim.Object is not null)
            {
                AddActor(claim.Object);
            }

            // A claim without an object (ListedUnder) is drawn as a loop to the actor's own
            // listing node when there is one, and is otherwise a property, not an edge.
            var to = claim.Object is not null ? "a:" + claim.Object.Id
                : claim.Subject!.SanctionDesignationId is { } d ? "d:" + d
                : null;
            if (to is null)
            {
                continue;
            }

            edges.Add(new GraphEdge
            {
                From = "a:" + claim.SubjectActorId,
                To = to,
                Kind = claim.Predicate.ToString(),
                Status = claim.Status switch
                {
                    ClaimStatus.Confirmed => GraphStatus.Confirmed,
                    ClaimStatus.Rejected => GraphStatus.Rejected,
                    _ => GraphStatus.Candidate,
                },
                Evidence = $"[{claim.Level}] {claim.Statement}",
                EvidenceUrls = claim.Evidence.Select(e => e.Url).Distinct().ToList(),
                Target = DecisionTarget.Claim,
                TargetId = claim.Id,
            });
        }

        return new DesignationGraph
        {
            SubjectId = graphId,
            Nodes = nodes.Values.ToList(),
            Edges = edges,
            Truncated = truncated,
        };
    }

    private static string ActorDetail(Actor a)
    {
        var kind = a.Kind switch
        {
            ActorKind.Person => a.IsPublicFigure ? "verejne činná osoba" : "osoba",
            ActorKind.Organisation => "organizácia",
            ActorKind.Website => "web",
            ActorKind.Channel => "kanál",
            ActorKind.MilitaryUnit => "vojenská jednotka",
            ActorKind.StateBody => "štátny orgán",
            _ => a.Kind.ToString(),
        };

        return string.IsNullOrWhiteSpace(a.Description) ? kind : $"{kind} · {a.Description}";
    }

    private IQueryable<Claim> Claims() => db.Claims.AsNoTracking()
        .Include(c => c.Subject)
        .Include(c => c.Object)
        .Include(c => c.Evidence)
        .AsSplitQuery();
}
