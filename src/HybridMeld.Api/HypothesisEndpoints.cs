using HybridMeld.Core.Domain;
using HybridMeld.Core.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace HybridMeld.Api;

/// <summary>
/// Hypotheses: questions with indicators, apart from cases. The same grant as the rest
/// of the workspace; nothing here reaches the feed, the facets or the graph.
///
///   GET  /hypotheses?status=&amp;q=, /hypotheses/{slug}
///   POST /hypotheses, PUT /hypotheses/{slug}
///   POST /hypotheses/{slug}/indicators, DELETE /hypotheses/{slug}/indicators/{id}
///   PUT  /hypotheses/{slug}/actors
///   POST /hypotheses/{slug}/decision
/// </summary>
internal static class HypothesisEndpoints
{
    public static IEndpointRouteBuilder MapHypotheses(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/hypotheses").WithTags("Hypotheses").RequireAuthorization(HybridMeldPolicies.User).AddEndpointFilter<WorkspaceErrorFilter>();

        group.MapGet("/", async (HypothesisService service, HypothesisStatus? status, string? q, int? limit, int? offset, CancellationToken ct) =>
            TypedResults.Ok(await service.ListAsync(status, q, limit ?? 50, offset ?? 0, ct)))
        .WithName("ListHypotheses")
        .WithSummary("Hypotheses, most recently changed first; status and a text search over title, question and rationale.");

        group.MapGet("/{slug}", async Task<Results<Ok<HypothesisView>, NotFound>> (HypothesisService service, string slug, CancellationToken ct) =>
            await service.GetAsync(slug, ct) is { } view ? TypedResults.Ok(view) : TypedResults.NotFound())
        .WithName("GetHypothesis")
        .WithSummary("One hypothesis with its indicators (quotes included) and the actors it points at.");

        group.MapPost("/", async (HypothesisService service, HypothesisInput input, HttpContext http, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input, WorkspaceEndpoints.Caller(http), ct);
            return TypedResults.Created($"/hypotheses/{created.Hypothesis.Slug}", created);
        })
        .WithName("CreateHypothesis")
        .WithSummary("A new, open hypothesis: title, the question, why it is worth asking, what would confirm and what would refute it.");

        group.MapPut("/{slug}", async (HypothesisService service, string slug, HypothesisInput input, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(slug, input, ct)))
        .WithName("UpdateHypothesis");

        group.MapPost("/{slug}/indicators", async (HypothesisService service, string slug, IndicatorInput input, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.AddIndicatorAsync(slug, input, WorkspaceEndpoints.Caller(http), ct)))
        .WithName("AddHypothesisIndicator")
        .WithSummary("A public fact with its source and verbatim quote; the page is kept when it can be.");

        group.MapDelete("/{slug}/indicators/{id:long}", async (HypothesisService service, string slug, long id, CancellationToken ct) =>
            TypedResults.Ok(await service.RemoveIndicatorAsync(slug, id, ct)))
        .WithName("RemoveHypothesisIndicator");

        group.MapPut("/{slug}/actors", async (HypothesisService service, string slug, HypothesisActorsInput input, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.SetActorsAsync(slug, input, WorkspaceEndpoints.Caller(http), ct)))
        .WithName("SetHypothesisActors")
        .WithSummary("The actors the hypothesis is about, as pointers: existing actors only, no claim and no edge.");

        group.MapPost("/{slug}/decision", async (HypothesisService service, string slug, HypothesisDecisionInput input, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.DecideAsync(slug, input, WorkspaceEndpoints.Caller(http), ct)))
        .WithName("DecideHypothesis")
        .WithSummary("Promoted (names the case written from it), Dismissed (with a note), or Open to take it back.");

        return endpoints;
    }
}
