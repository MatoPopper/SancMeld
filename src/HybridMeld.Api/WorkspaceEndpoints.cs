using HybridMeld.Core.Domain;
using HybridMeld.Core.Graph;
using HybridMeld.Core.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SancMeld.Core.Relations;

namespace HybridMeld.Api;

/// <summary>
/// Cases, actors, claims, the graph and the feed. Everything needs the grant; everyone
/// with it reads and writes (one flag, no editor role: user's decision of 2026-10-21).
/// Who did what is taken from the sign-in, never from a form field.
///
///   GET  /campaigns, /campaigns/{slug}, /campaigns/{slug}/graph
///   POST /campaigns, PUT /campaigns/{slug}, POST /campaigns/{slug}/editorial
///   GET  /actors?q=, /actors/{id}, /actors/{id}/graph
///   POST /actors, PUT /actors/{id}, PUT /actors/{id}/links
///   GET  /claims/{id}; POST /claims; PUT /claims/{id}; POST /claims/{id}/evidence, /claims/{id}/decision
///   GET  /feed?since=
/// </summary>
internal static class WorkspaceEndpoints
{
    public static IEndpointRouteBuilder MapWorkspace(this IEndpointRouteBuilder endpoints)
    {
        var campaigns = endpoints.MapGroup("/campaigns").WithTags("Cases").RequireAuthorization(HybridMeldPolicies.User).AddEndpointFilter<WorkspaceErrorFilter>();

        campaigns.MapGet("/", async (CampaignService service,
            DateTimeOffset? since, string? countries, string? kinds, bool? touchesSanctions, bool? touchesPep, EvidenceLevel? minLevel, bool? confirmedOnly,
            bool? includeSeed, bool? includeDrafts, bool? includeClosed, string? q, int? limit, int? offset, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(
                CampaignFilter.Parse(since, countries, kinds, touchesSanctions, touchesPep, minLevel, confirmedOnly, includeSeed, includeDrafts, includeClosed, q),
                limit ?? 50, offset ?? 0, ct)))
        .WithName("ListCampaigns")
        .WithSummary("Cases through the filter bar, most recently changed first.")
        .WithDescription(
            "countries and kinds are comma-separated lists; touchesSanctions/touchesPep keep only cases whose actor is linked to a " +
            "sanctions record / a PEP person; minLevel keeps cases with a live claim at that level or stronger; confirmedOnly makes " +
            "only confirmed claims count. Defaults: published cases, closed included, the regime lists seeded from the sanctions data excluded.");

        campaigns.MapGet("/facets", async (CampaignService service,
            DateTimeOffset? since, string? countries, string? kinds, bool? touchesSanctions, bool? touchesPep, EvidenceLevel? minLevel, bool? confirmedOnly,
            bool? includeSeed, bool? includeDrafts, bool? includeClosed, string? q, CancellationToken ct) =>
            TypedResults.Ok(await service.FacetsAsync(
                CampaignFilter.Parse(since, countries, kinds, touchesSanctions, touchesPep, minLevel, confirmedOnly, includeSeed, includeDrafts, includeClosed, q), ct)))
        .WithName("GetCampaignFacets")
        .WithSummary("How many cases each kind, country and overlap chip would show under the other filters.");

        campaigns.MapGet("/{slug}", async Task<Results<Ok<CampaignView>, NotFound>> (CampaignService service, string slug, CancellationToken ct) =>
            await service.GetAsync(slug, ct) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound())
        .WithName("GetCampaign")
        .WithSummary("One case with every actor and every claim, evidence included.");

        campaigns.MapGet("/{slug}/graph", async Task<Results<Ok<DesignationGraph>, NotFound>> (HybridGraphBuilder graphs, string slug, CancellationToken ct) =>
            await graphs.ForCampaignAsync(slug, ct) is { } graph ? TypedResults.Ok(graph) : TypedResults.NotFound())
        .WithName("GetCampaignGraph")
        .WithSummary("The case as a graph in the sanctions graph's shape: actors as nodes, claims as edges, linked listings and PEP records as nodes with a route out.");

        campaigns.MapPost("/", async (CampaignService service, CampaignInput input, HttpContext http, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input, Caller(http), ct);
            return TypedResults.Created($"/campaigns/{created.Slug}", created);
        })
        .WithName("CreateCampaign")
        .WithSummary("A new case, as a draft. The slug is chosen now and kept.");

        campaigns.MapPut("/{slug}", async (CampaignService service, string slug, CampaignInput input, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(slug, input, ct)))
        .WithName("UpdateCampaign");

        campaigns.MapPost("/{slug}/editorial", async (CampaignService service, string slug, EditorialInput input, CancellationToken ct) =>
            TypedResults.Ok(await service.SetEditorialAsync(slug, input, ct)))
        .WithName("SetCampaignEditorial")
        .WithSummary("Draft, Published or Withdrawn (with a reason).");

        var actors = endpoints.MapGroup("/actors").WithTags("Actors").RequireAuthorization(HybridMeldPolicies.User).AddEndpointFilter<WorkspaceErrorFilter>();

        actors.MapGet("/", async (ActorService service, string? q, ActorKind? kind, int? limit, CancellationToken ct) =>
            TypedResults.Ok(await service.SearchAsync(q, kind, limit ?? 50, ct)))
        .WithName("SearchActors")
        .WithSummary("Actors by name or alias, in the sanctions normal form: 'sandworm' reaches GRU unit 74455.");

        actors.MapGet("/{id:guid}", async Task<Results<Ok<ActorView>, NotFound>> (ActorService service, Guid id, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound())
        .WithName("GetActor")
        .WithSummary("One actor with aliases, links, every case and every claim naming them.");

        actors.MapGet("/{id:guid}/graph", async Task<Results<Ok<DesignationGraph>, NotFound>> (HybridGraphBuilder graphs, Guid id, CancellationToken ct) =>
            await graphs.ForActorAsync(id, ct) is { } graph ? TypedResults.Ok(graph) : TypedResults.NotFound())
        .WithName("GetActorGraph")
        .WithSummary("The actor, their ties and their neighbours' ties, one step out.");

        actors.MapPost("/", async (ActorService service, ActorInput input, HttpContext http, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input, Caller(http), ct);
            return TypedResults.Created($"/actors/{created.Actor.Id}", created);
        })
        .WithName("CreateActor");

        actors.MapPut("/{id:guid}", async (ActorService service, Guid id, ActorInput input, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
        .WithName("UpdateActor");

        actors.MapPut("/{id:guid}/links", async (ActorService service, Guid id, ActorLinkInput input, CancellationToken ct) =>
            TypedResults.Ok(await service.SetLinksAsync(id, input, ct)))
        .WithName("SetActorLinks")
        .WithSummary("Link the actor to a sanctions record and/or a PEP person by id. One-way: nothing in those services points back.");

        var claims = endpoints.MapGroup("/claims").WithTags("Claims").RequireAuthorization(HybridMeldPolicies.User).AddEndpointFilter<WorkspaceErrorFilter>();

        claims.MapGet("/{id:long}", async Task<Results<Ok<ClaimView>, NotFound>> (ClaimService service, long id, CancellationToken ct) =>
            await service.GetAsync(id, ct) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound())
        .WithName("GetClaim");

        claims.MapPost("/", async (ClaimService service, ClaimInput input, HttpContext http, CancellationToken ct) =>
        {
            // A program with a key is an agent; a person is an analyst. The claim says which.
            var foundBy = HybridMeldClaims.IsApiKey(http.User) ? FoundBy.Agent : FoundBy.Analyst;
            var created = await service.CreateAsync(input, Caller(http), foundBy, ct);
            return TypedResults.Created($"/claims/{created.Id}", created);
        })
        .WithName("CreateClaim")
        .WithSummary("A new claim, as a candidate, with at least one quote; or from an inbox item, which then counts as the quote.");

        claims.MapPut("/{id:long}", async (ClaimService service, long id, ClaimUpdateInput input, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(id, input, ct)))
        .WithName("UpdateClaim");

        claims.MapPost("/{id:long}/evidence", async (ClaimService service, long id, EvidenceInput input, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.AddEvidenceAsync(id, input, Caller(http), ct)))
        .WithName("AddClaimEvidence")
        .WithSummary("Another source for the claim; the page is fetched and kept when it can be.");

        claims.MapPost("/{id:long}/decision", async (ClaimService service, long id, ClaimDecisionInput input, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.DecideAsync(id, input, Caller(http), ct)))
        .WithName("DecideClaim")
        .WithSummary("Confirmed (the quote is in the source and the sentence does not exceed it), Rejected, Withdrawn (with a note), or Candidate to take it back.");

        endpoints.MapGet("/feed", async (FeedService feed,
            DateTimeOffset? since, string? countries, string? kinds, bool? touchesSanctions, bool? touchesPep, EvidenceLevel? minLevel, bool? confirmedOnly,
            bool? includeSeed, bool? includeDrafts, int? limit, CancellationToken ct) =>
            TypedResults.Ok(await feed.SinceAsync(
                CampaignFilter.Parse(since, countries, kinds, touchesSanctions, touchesPep, minLevel, confirmedOnly, includeSeed, includeDrafts, true, null),
                limit ?? 50, ct)))
        .WithTags("Feed")
        .RequireAuthorization(HybridMeldPolicies.User)
        .WithName("GetFeed")
        .WithSummary("Cases and live claims changed since a moment (default: the last 30 days) through the same filter bar as /campaigns, newest first.");

        return endpoints;
    }

    internal static string Caller(HttpContext http) => HybridMeldClaims.EmailOf(http.User) ?? "unknown";
}

/// <summary>Turns a refused request into its status and a body with the code, instead of a 500.</summary>
internal sealed class WorkspaceErrorFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(next);
        try
        {
            return await next(context).ConfigureAwait(false);
        }
        catch (WorkspaceException ex)
        {
            return Results.Json(new ApiError(ex.Code, ex.Message), statusCode: ex.Status);
        }
    }
}
