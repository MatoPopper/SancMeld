using HybridMeld.Core.Domain;
using HybridMeld.Core.Sources;
using HybridMeld.Core.Workspace;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;

namespace HybridMeld.Api;

/// <summary>
/// Watched sources and the inbox.
///
///   GET  /sources; POST /sources; PUT /sources/{code}; POST /sources/{code}/fetch
///   GET  /inbox?status=&amp;source=&amp;q=; GET /inbox/{id}; POST /inbox/{id}/triage
/// </summary>
internal static class SourceEndpoints
{
    public static IEndpointRouteBuilder MapSources(this IEndpointRouteBuilder endpoints)
    {
        var sources = endpoints.MapGroup("/sources").WithTags("Sources").RequireAuthorization(HybridMeldPolicies.User).AddEndpointFilter<WorkspaceErrorFilter>();

        sources.MapGet("/", async (SourceService service, CancellationToken ct) => TypedResults.Ok(await service.ListAsync(ct)))
        .WithName("ListSources")
        .WithSummary("Every watched source with its last run and how many of its items wait in the inbox.");

        sources.MapPost("/", async (SourceService service, SourceInput input, HttpContext http, CancellationToken ct) =>
        {
            var created = await service.CreateAsync(input, WorkspaceEndpoints.Caller(http), ct);
            return TypedResults.Created($"/sources/{created.Code}", created);
        })
        .WithName("CreateSource")
        .WithSummary("Watch a feed, a Telegram channel (its public t.me page) or a web page. Anyone with the grant may; the row says who.");

        sources.MapPut("/{code}", async (SourceService service, string code, SourceInput input, CancellationToken ct) =>
            TypedResults.Ok(await service.UpdateAsync(code.ToUpperInvariant(), input, ct)))
        .WithName("UpdateSource");

        sources.MapPost("/{code}/fetch", async (SourceFetchService fetch, string code, CancellationToken ct) =>
            TypedResults.Ok(await fetch.RunAsync(code.ToUpperInvariant(), ct)))
        .WithName("FetchSource")
        .WithSummary("Fetch the source now instead of waiting for the scheduler.");

        var inbox = endpoints.MapGroup("/inbox").WithTags("Inbox").RequireAuthorization(HybridMeldPolicies.User).AddEndpointFilter<WorkspaceErrorFilter>();

        inbox.MapGet("/", async (SourceService service, SourceItemStatus? status, string? source, string? q, int? limit, int? offset, CancellationToken ct) =>
            TypedResults.Ok(await service.InboxAsync(status, source, q, limit ?? 50, offset ?? 0, ct)))
        .WithName("ListInbox")
        .WithSummary("Fetched items, newest first; status New by default is what waits to be read.");

        inbox.MapGet("/{id:guid}", async Task<Results<Ok<SourceItemView>, NotFound>> (SourceService service, Guid id, CancellationToken ct) =>
            await service.GetItemAsync(id, ct) is { } item ? TypedResults.Ok(item) : TypedResults.NotFound())
        .WithName("GetInboxItem");

        inbox.MapPost("/{id:guid}/triage", async (SourceService service, Guid id, TriageInput input, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await service.TriageAsync(id, input, WorkspaceEndpoints.Caller(http), ct)))
        .WithName("TriageInboxItem")
        .WithSummary("Mark an item Triaged or Ignored; Linked happens by making a claim from it (POST /claims with sourceItemId).");

        return endpoints;
    }
}
