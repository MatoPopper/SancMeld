using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using PepMeld.Core.Checks;
using PepMeld.Core.Lists;

namespace PepMeld.Api;

/// <summary>
/// The asynchronous check:
///
///   POST /checks           start one; 202 with the id, 409 while another is running
///   GET  /checks/{id}      its state and findings so far
///   GET  /checks/current   the running one, or 204
///   GET  /checks           the caller's history
///   GET  /sources          what the lists are and when they were last imported
///
/// Results are partial on purpose: every stage writes its findings as it finishes, so a
/// client polling the id sees the lists within seconds while a later stage still runs.
/// </summary>
internal static class CheckEndpoints
{
    public static IEndpointRouteBuilder MapChecks(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/checks").WithTags("Checks").RequireAuthorization(PepMeldPolicies.User);

        group.MapPost("/", async Task<Results<Accepted<CheckView>, BadRequest<ApiError>, Conflict<ApiError>, UnauthorizedHttpResult>> (
            CheckService checks, NewCheck request, HttpContext http, CancellationToken ct) =>
        {
            var email = PepMeldClaims.EmailOf(http.User);
            if (string.IsNullOrEmpty(email))
            {
                return TypedResults.Unauthorized();
            }

            var result = await checks.CreateAsync(email, request, ct);

            return result.Outcome switch
            {
                CreateOutcome.Created => TypedResults.Accepted($"/checks/{result.Check!.CheckId}", result.Check),
                CreateOutcome.AlreadyRunning => TypedResults.Conflict(new ApiError("check_in_progress", result.Message!, result.Check?.CheckId)),
                _ => TypedResults.BadRequest(new ApiError("invalid_request", result.Message ?? "Invalid request.")),
            };
        })
        .AddEndpointFilter<RequestBudgetFilter>()
        .WithName("StartCheck")
        .WithSummary("Start a check of one person. The date of birth is mandatory.")
        .WithDescription(
            "Returns 202 with the check at once; poll GET /checks/{id} for the stages and findings. " +
            "A user has at most one check running: a second POST is answered with 409 and the running check's id. " +
            "Each accepted check spends one request of the caller's budget (X-Requests-Remaining).");

        group.MapGet("/", async (CheckService checks, int? limit, HttpContext http, CancellationToken ct) =>
            TypedResults.Ok(await checks.ListAsync(PepMeldClaims.EmailOf(http.User)!, limit ?? 20, ct)))
        .WithName("ListChecks")
        .WithSummary("The caller's checks, newest first.");

        group.MapGet("/current", async Task<Results<Ok<CheckView>, NoContent>> (CheckService checks, HttpContext http, CancellationToken ct) =>
            await checks.CurrentAsync(PepMeldClaims.EmailOf(http.User)!, ct) is { } current
                ? TypedResults.Ok(current)
                : TypedResults.NoContent())
        .WithName("GetCurrentCheck")
        .WithSummary("The caller's running check, or 204 when none is.");

        group.MapGet("/{id:guid}", async Task<Results<Ok<CheckView>, NotFound>> (CheckService checks, Guid id, HttpContext http, CancellationToken ct) =>
            await checks.GetAsync(PepMeldClaims.EmailOf(http.User)!, id, ct) is { } check
                ? TypedResults.Ok(check)
                : TypedResults.NotFound())
        .WithName("GetCheck")
        .WithSummary("One check with its stages and findings so far. Another user's check is 404, not 403.");

        return endpoints;
    }

    public static IEndpointRouteBuilder MapSources(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/sources", async (SourceQueryService sources, CancellationToken ct) =>
            TypedResults.Ok(await sources.ListAsync(ct)))
        .WithTags("Sources")
        .WithName("GetSources")
        .WithSummary("The lists this instance holds, with the last import and what each publishes.")
        .WithDescription(
            "Open to read: it describes coverage, not people. A source without a date of birth " +
            "can only ever yield level C findings; a source with no import yet was not searched at all.");

        return endpoints;
    }
}
