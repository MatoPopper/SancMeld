using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using SancMeld.Core.Access;
using SancMeld.Core.Domain;
using SancMeld.Core.Learning;
using SancMeld.Core.Relations;
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

            return TypedResults.Ok(await queries.SearchAsync(q, limit ?? 25, ct: ct));
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

        group.MapGet("/mentions", async Task<Results<Ok<MentionPage>, BadRequest<string>>> (
            DesignationQueryService queries, string? q, int? limit, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(q))
            {
                return TypedResults.BadRequest("Supply q.");
            }

            return TypedResults.Ok(await queries.MentionsAsync(q, limit ?? 10, ct));
        })
        .WithName("SearchMentions")
        .WithSummary("Listings whose statement of reasons mentions the words — the owners of a company no list names.")
        .WithDescription(
            "Word search over the free text of every current listing (reason, function, title, " +
            "remarks); every word of the query must occur, and quoted phrases are kept together. " +
            "Not a name match and not scored as one: a company found here is on no list, and " +
            "whether the listed person owns or controls it is what the statement says and the " +
            "list does not decide.");

        group.MapGet("/recent", async (DesignationGraphService graphs, int? runs, int? perRun, CancellationToken ct) =>
            TypedResults.Ok(await graphs.RecentAsync(runs ?? 5, perRun ?? 25, ct)))
        .WithName("GetRecentListings")
        .WithSummary("What the last import runs added, and what each new listing touches.")
        .WithDescription(
            "The runs that inserted or changed listings, newest first, each with its new subjects " +
            "and, per subject, the other lists naming them, the parties a register ties them to, " +
            "the listings whose statements mention them and the listings their own statement names. " +
            "Each tie carries a status: confirmed (a publisher's or an analyst's word), candidate " +
            "(a machine's, with evidence), review (a candidate that reaches a binding listing or a " +
            "controlling share), rejected.");

        group.MapGet("/{id:guid}/graph", async Task<Results<Ok<DesignationGraph>, NotFound>> (
            DesignationGraphService graphs, Guid id, CancellationToken ct) =>
            await graphs.BuildAsync(id, ct) is { } graph
                ? TypedResults.Ok(graph)
                : TypedResults.NotFound())
        .WithName("GetDesignationGraph")
        .WithSummary("The listing and everything it is tied to, as nodes and edges with evidence.")
        .WithDescription(
            "Cross-references between lists, related parties from open registers and the listed " +
            "subjects tied to the same parties, and mentions in statements of reasons in both " +
            "directions. Every edge says why it is drawn and which of the four statuses it has; " +
            "the graph decides nothing.");

        group.MapGet("/{id:guid}/related", async (RelatedPartyQueryService related, Guid id, CancellationToken ct) =>
            TypedResults.Ok(await related.ForDesignationAsync(id, ct)))
        .WithName("GetDesignationRelatedParties")
        .WithSummary("What open registers say this subject owns, founded or runs.")
        .WithDescription(
            "Wikidata's statements about the listed person, with the evidence the register cites " +
            "and the evidence for taking the register's person to be the listed one (name match and " +
            "date of birth). Candidates until an analyst confirms them; advisory by construction, " +
            "because no sanctions list names these parties.");

        return endpoints;
    }

    public static IEndpointRouteBuilder MapScreening(this IEndpointRouteBuilder endpoints, bool requireSignIn)
    {
        endpoints.MapPost("/check", async Task<Results<Ok<CheckResult>, BadRequest<string>>> (
            ComplianceCheckService check, CheckQuery query, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (query is null || string.IsNullOrWhiteSpace(query.Name))
            {
                return TypedResults.BadRequest("Supply at least a name.");
            }

            return TypedResults.Ok(await check.CheckAsync(WithCaller(query, user), ct));
        })
        .RequireWrite(requireSignIn)
        .AddEndpointFilter<RequestBudgetFilter>()
        .WithTags("Screening")
        .WithName("Check")
        .WithSummary("Screen a name and get an answer a compliance officer can act on.")
        .WithDescription(
            "The same evidence as /screen, presented as a decision rather than a score. A " +
            "percentage is not something anyone can sign off on; what is actionable is which " +
            "of four situations you are in — a document number matched exactly, the details " +
            "you gave agree with the listing, the listing knows nothing that could confirm or " +
            "rule it out, or the listing states something that disagrees. " +
            "The third is the common case and the honest one: most listings carry no date of " +
            "birth and no document, so the list cannot settle it and your own records have to. " +
            "Subjects appearing on several lists are gathered into one entry, and a listing " +
            "the name search could not reach is pulled in when a cross-reference points at it.");

        endpoints.MapScreeningDiagnostics(requireSignIn);

        return endpoints;
    }

    /// <summary>
    /// Capturing what the analyst decided, and handing the corpus out for training.
    ///
    /// Two endpoints, not three. Recording a decision belongs to the compliance workflow;
    /// exporting the corpus is a different job with a different consumer. Training itself
    /// is deliberately NOT an endpoint — a model that can be retrained by an HTTP call is
    /// a model nobody can audit. It is an offline job producing a versioned artefact,
    /// which arrives here as a new row in match_algorithm.
    /// </summary>
    /// <summary>A company to look up in a national register: an identification number or a name.</summary>
    public sealed record RegisterLookupRequest(string Query, int? Depth = null);

    /// <summary>A listing to look up in the Slovak business register.</summary>
    public sealed record SkRegisterLookupRequest(Guid DesignationId);

    /// <summary>
    /// An analyst's verdict on a link the graph shows: a listed subject's tie to a party,
    /// a tie between two parties, or the identity a register match rests on.
    /// </summary>
    public static IEndpointRouteBuilder MapRelationDecisions(this IEndpointRouteBuilder endpoints, bool requireSignIn)
    {
        endpoints.MapPost("/registers/ares/lookup", async Task<Results<Ok<SancMeld.Connectors.Ares.AresLookupResult>, BadRequest<string>>> (
            SancMeld.Connectors.Ares.AresLookupService ares, RegisterLookupRequest request, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Query))
            {
                return TypedResults.BadRequest("Supply an IČO or a company name.");
            }

            return TypedResults.Ok(await ares.LookupAsync(request.Query, request.Depth, ct));
        })
        .RequireWrite(requireSignIn)
        .AddEndpointFilter<RequestBudgetFilter>()
        .WithTags("Designations")
        .WithName("LookupAres")
        .WithSummary("Read a Czech company's shareholders and statutory body from ARES and link the listed ones — writes.")
        .WithDescription(
            "Query is an IČO or a business name. Shareholders and members of the statutory body " +
            "whose date of birth and name match a listed person are linked to the company as " +
            "candidates; corporate shareholders are followed up to Depth hops. People who are not " +
            "listed are not stored. The query is sent to ARES (Ministry of Finance of the Czech Republic).");

        endpoints.MapPost("/registers/sk/lookup", async Task<Results<Ok<SancMeld.Connectors.SkRegister.SkRegisterLookupResult>, BadRequest<string>, StatusCodeHttpResult>> (
            SancMeld.Connectors.SkRegister.SkRegisterLookupService sk, IAdditionalInfoAccess access, SkRegisterLookupRequest request, CancellationToken ct) =>
        {
            // What it finds is additional information; asking for it is too. A plain 403
            // rather than Forbid(): a host without sign-in has no scheme to challenge with.
            if (!await access.AllowedAsync(ct))
            {
                return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
            }

            if (request is null || request.DesignationId == Guid.Empty)
            {
                return TypedResults.BadRequest("Supply the listing's designationId.");
            }

            return TypedResults.Ok(await sk.LookupAsync(request.DesignationId, null, ct, fresh: true));
        })
        .RequireWrite(requireSignIn)
        .AddEndpointFilter<RequestBudgetFilter>()
        .WithTags("Designations")
        .WithName("LookupSkRegister")
        .WithSummary("Ask the Slovak business register for namesakes of a listed subject — writes; additional information only.")
        .WithDescription(
            "A person is searched in ORSR by surname and given name; the companies of every matching " +
            "shareholder or director are read from RPO with the person's role, dates and the country of " +
            "their address. A company is searched in RPO by its most distinctive word. Neither register " +
            "publishes a date of birth: every link is a candidate lead, visible only to users allowed the " +
            "additional information, and only they may call this. Queries go to orsr.sk and api.statistics.sk.");

        endpoints.MapGet("/relations/review", async Task<Results<Ok<ReviewQueueView>, StatusCodeHttpResult>> (
            ReviewQueueService queue, IAdditionalInfoAccess access, LinkStatus? status, string? system, int? limit, CancellationToken ct) =>
        {
            // The connections search is a working screen for those who read leads, not a
            // public list: the same people who see the additional information see it.
            if (!await access.AllowedAsync(ct))
            {
                return TypedResults.StatusCode(StatusCodes.Status403Forbidden);
            }

            return TypedResults.Ok(await queue.ListAsync(status ?? LinkStatus.Candidate, system, limit ?? 200, ct));
        })
        .WithTags("Designations")
        .WithName("GetReviewQueue")
        .WithSummary("Every tie between a listing and a party a person still has to decide, the most urgent first.")
        .WithDescription(
            "Status Candidate (default), Confirmed or Rejected; system narrows to one register (WIKIDATA, ARES, " +
            "RPVS, CURATED, SK_ORSR, OSINT). Review first — a candidate that reaches a binding listing or claims " +
            "control — then candidates, a Slovak-register name match whose address is in the listing's country " +
            "before one whose is not. Only for callers allowed the additional information; 403 otherwise. " +
            "Decide an item with POST /relations/decisions, target ListingRelation and its id.");

        endpoints.MapPost("/relations/decisions", async Task<Results<Ok<RelationDecisionResult>, BadRequest<string>, NotFound<string>>> (
            RelationDecisionService decisions, RelationDecisionRequest request, ClaimsPrincipal user, CancellationToken ct) =>
        {
            // The token wins over the body, as for adjudications.
            var decidedBy = SancMeldClaims.EmailOf(user) ?? request.DecidedBy;

            try
            {
                return TypedResults.Ok(await decisions.DecideAsync(request with { DecidedBy = decidedBy }, ct));
            }
            catch (KeyNotFoundException ex)
            {
                return TypedResults.NotFound(ex.Message);
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
        .RequireWrite(requireSignIn)
        .WithTags("Designations")
        .WithName("DecideRelation")
        .WithSummary("Confirm, reject or reopen a link shown in the graph — writes.")
        .WithDescription(
            "Target is ListingRelation, PartyRelation or Identity, with the id the graph edge carries. " +
            "Decision is Confirmed, Rejected, or Candidate to take a decision back. The latest decision " +
            "is on the link; every decision is also appended to relation_decision with who and when.");

        return endpoints;
    }

    public static IEndpointRouteBuilder MapAdjudications(this IEndpointRouteBuilder endpoints, bool requireSignIn)
    {
        var group = endpoints.MapGroup("/adjudications").WithTags("Adjudications");

        group.MapGet("/reasons", async (AdjudicationService adjudications, CancellationToken ct) =>
            TypedResults.Ok(await adjudications.ReasonsAsync(ct)))
        .WithName("GetAdjudicationReasons")
        .WithSummary("The reasons an analyst can give for a decision.")
        .WithDescription(
            "Show these as the choices. Free text cannot be counted, and 'why not' is both " +
            "the part a supervisor asks about and the part a model learns from.");

        group.MapPost("/", async Task<Results<Ok<AdjudicationResult>, BadRequest<string>>> (
            AdjudicationService adjudications, AdjudicationRequest request, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request is null || request.Decisions.Count == 0)
            {
                return TypedResults.BadRequest("Supply at least one decision.");
            }

            // The token wins over the body. Who decided is the one field of an audit trail
            // that must not be assertable by whoever is writing to it — otherwise a signed-in
            // analyst can file a decision under a colleague's name.
            var decidedBy = SancMeldClaims.EmailOf(user) ?? request.DecidedBy;

            if (string.IsNullOrWhiteSpace(decidedBy))
            {
                return TypedResults.BadRequest("Supply who decided. An unattributed decision is not an audit trail.");
            }

            try
            {
                return TypedResults.Ok(await adjudications.RecordAsync(request with { DecidedBy = decidedBy }, ct));
            }
            catch (InvalidOperationException ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
        .RequireWrite(requireSignIn)
        .WithName("RecordAdjudication")
        .WithSummary("Record what the analyst decided about a check.")
        .WithDescription(
            "One call per review, carrying every decision made in it — a half-recorded review " +
            "is exactly the state nobody can reconstruct. Decisions are per listing, not per " +
            "subject: a person on the EU and the UN lists is two listings and two legal facts. " +
            "Undecided and NeedsMoreData are recorded but not labelled; the analyst did not say " +
            "no, they said they could not tell. Corrections are new rows carrying 'supersedes'.");

        return endpoints;
    }

    /// <summary>
    /// The labelled corpus, for training a model outside this system. Carries feature
    /// vectors and labels, never a name or a query — a training set is copied to wherever
    /// the training happens, and it must not become a copy of somebody's customer file.
    /// </summary>
    public static IEndpointRouteBuilder MapTrainingData(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapGet("/training/samples", async (
            AdjudicationService adjudications, long? afterId, int? limit, string? origin, CancellationToken ct) =>
            TypedResults.Ok(await adjudications.SamplesAsync(afterId ?? 0, limit ?? 1000, origin, ct)))
        .WithTags("Adjudications")
        .WithName("GetTrainingSamples")
        .WithSummary("The labelled comparisons, for training a model elsewhere.")
        .WithDescription(
            "Paged by id — pass the last id back as afterId — so an export can be resumed and " +
            "repeated without missing or duplicating a row. Filter by origin: ADJUDICATION for " +
            "analyst decisions, PUBLIC_LIST_XREF for pairs derived from publishers' own " +
            "cross-references. No personal data is included, by design.");

        return endpoints;
    }

    /// <summary>
    /// The raw form: scores, feature vectors, and every candidate considered. Kept for the
    /// audit trail and for whoever is tuning the matcher — not for the person signing off.
    /// </summary>
    private static IEndpointRouteBuilder MapScreeningDiagnostics(this IEndpointRouteBuilder endpoints, bool requireSignIn)
    {
        endpoints.MapPost("/screen", async Task<Results<Ok<ScreeningResult>, BadRequest<string>>> (
            ScreeningService screening, ScreeningQuery query, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (query is null || string.IsNullOrWhiteSpace(query.Name))
            {
                return TypedResults.BadRequest("Supply at least a name.");
            }

            return TypedResults.Ok(await screening.ScreenAsync(WithCaller(query, user), ct));
        })
        .RequireWrite(requireSignIn)
        .AddEndpointFilter<RequestBudgetFilter>()
        .WithTags("Screening")
        .WithName("Screen")
        .WithSummary("Screen a name, with whatever else is known about the subject. Writes.")
        .WithDescription(
            "Supplying a birth year, country or identifier sharpens the result but is never " +
            "required. Each qualifier is answered separately as agrees, contradicts, unknown " +
            "on the listing, or not provided — a listing that simply does not record a birth " +
            "date is not treated as one that disagrees about it. " +
            "Candidates below the threshold are returned too, flagged, so nothing that was " +
            "considered is hidden from the person who has to sign off.");

        return endpoints;
    }

    /// <summary>
    /// Behind the sign-in where there is one, open where there is not.
    ///
    /// The endpoints that write are the ones worth protecting: reading the aggregate takes
    /// nothing from anybody, while every screening and every adjudication leaves rows in
    /// this database attributed to someone. A host with no sign-in configured keeps the
    /// unauthenticated behaviour the open-source edition documents — requiring a policy
    /// against a scheme nobody registered would fail every call with a 500.
    /// </summary>
    private static RouteHandlerBuilder RequireWrite(this RouteHandlerBuilder builder, bool requireSignIn) =>
        requireSignIn ? builder.RequireAuthorization(SancMeldPolicies.Write) : builder;

    /// <summary>
    /// Stamps the caller from the token, overriding whatever the body claimed.
    ///
    /// requestedBy stays in the request model because an unauthenticated host has no other
    /// way to say who asked — but where a token exists it is the better answer, and a
    /// self-declared identity next to a proven one is just a way to file work under
    /// somebody else's name.
    /// </summary>
    private static CheckQuery WithCaller(CheckQuery query, ClaimsPrincipal user) =>
        SancMeldClaims.EmailOf(user) is { Length: > 0 } email ? query with { RequestedBy = email } : query;

    private static ScreeningQuery WithCaller(ScreeningQuery query, ClaimsPrincipal user) =>
        SancMeldClaims.EmailOf(user) is { Length: > 0 } email ? query with { RequestedBy = email } : query;
}
