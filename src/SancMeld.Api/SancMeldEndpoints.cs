using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SancMeld.Core.Domain;
using SancMeld.Core.Search;

namespace SancMeld.Api;

/// <summary>
/// The three ways the aggregate is read.
///
///   GET  /designations        everything, page by page, for mirroring the aggregate
///   GET  /designations/search by name, trigram, cheap and wide
///   POST /screen              a name plus whatever else is known, scored and recorded
///
/// The first two are pure reads. The third writes: every screening is stored with its
/// candidates and their feature vectors, because a compliance decision that cannot be
/// reconstructed is not a decision anyone can stand behind — and because those records
/// are what a matching model will eventually be trained on.
/// </summary>
internal static class SancMeldEndpoints
{
    public static IEndpointRouteBuilder MapDesignations(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/designations").WithTags("Designations");

        group.MapGet("/", async (
            DesignationQueryService queries,
            Guid? cursor,
            int? pageSize,
            string? source,
            SubjectType? subjectType,
            bool? includePossiblyMissing,
            CancellationToken ct) =>
        {
            var page = await queries.GetAllAsync(
                cursor,
                pageSize ?? DesignationQueryService.DefaultPageSize,
                source,
                subjectType,
                includePossiblyMissing ?? true,
                ct);

            return TypedResults.Ok(page);
        })
        .WithName("GetDesignations")
        .WithSummary("Every current listing, page by page.")
        .WithDescription(
            "Paged by cursor rather than page number: an import that inserts a record shifts " +
            "every offset, so a client walking the list by number would skip subjects. " +
            "Listings with no date of birth, no identifier or no address are included — on the " +
            "EU list that is most of them, and an incomplete listing is no less binding.");

        group.MapGet("/search", async Task<Results<Ok<IReadOnlyList<SearchHit>>, BadRequest<string>>> (
            DesignationQueryService queries,
            string q,
            int? limit,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return TypedResults.BadRequest("Supply a name in 'q'.");
            }

            return TypedResults.Ok(await queries.SearchAsync(q, limit ?? 25, ct));
        })
        .WithName("SearchDesignations")
        .WithSummary("Full-text search over every recorded spelling.")
        .WithDescription(
            "Trigram similarity over primary names, aliases and transliterations alike. " +
            "Character-level rather than word-level, because 19 % of genuinely matching name " +
            "pairs on the EU list share no exact token and a tokenised index cannot reach them.");

        group.MapGet("/{id:guid}", async Task<Results<Ok<DesignationView>, NotFound>> (
            DesignationQueryService queries, Guid id, CancellationToken ct) =>
            await queries.GetAsync(id, ct) is { } designation
                ? TypedResults.Ok(designation)
                : TypedResults.NotFound())
        .WithName("GetDesignation");

        return endpoints;
    }

    public static IEndpointRouteBuilder MapScreening(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/screen", async Task<Results<Ok<ScreeningResult>, BadRequest<string>>> (
            ScreeningService screening, ScreeningQuery query, CancellationToken ct) =>
        {
            if (query is null || string.IsNullOrWhiteSpace(query.Name))
            {
                return TypedResults.BadRequest("Supply at least a name.");
            }

            return TypedResults.Ok(await screening.ScreenAsync(query, ct));
        })
        .WithTags("Screening")
        .WithName("Screen")
        .WithSummary("Screen a name, with whatever else is known about the subject.")
        .WithDescription(
            "Supplying a birth year, country or identifier sharpens the result but is never " +
            "required. Each qualifier is answered separately as agrees, contradicts, unknown " +
            "on the listing, or not provided — a listing that simply does not record a birth " +
            "date is not treated as one that disagrees about it. " +
            "Candidates below the threshold are returned too, flagged, so nothing that was " +
            "considered is hidden from the person who has to sign off.");

        return endpoints;
    }
}
