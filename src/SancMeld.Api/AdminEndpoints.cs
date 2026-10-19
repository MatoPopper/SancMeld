using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Access;

namespace SancMeld.Api;

/// <summary>What the front end needs in order to start a sign-in. Nothing here is secret.</summary>
public sealed record AuthConfiguration(string GoogleClientId);

/// <summary>Who the bearer token says the caller is, and whether this instance knows them.</summary>
public sealed record SignedInUser(string Email, string? Name, string? Subject, bool IsAdmin)
{
    /// <summary>Whether this caller may screen: any verified address.</summary>
    public bool CanWrite { get; init; }

    /// <summary>The request budget and what is left of it; null on an unverified address.</summary>
    public int? RequestLimit { get; init; }
    public int? RequestsUsed { get; init; }
    public int? RequestsRemaining { get; init; }

    /// <summary>A request for more that is waiting for the operator, if the caller sent one.</summary>
    public DateTimeOffset? PendingQuotaRequestAt { get; init; }

    /// <summary>
    /// Whether the caller sees the additional information: leads that rest on a name alone
    /// (the Slovak business register) and open-source research. Granted by the operator.
    /// </summary>
    public bool CanSeeAdditionalInfo { get; init; }

    /// <summary>May open the users screen.</summary>
    public bool IsUserAdmin { get; init; }

    /// <summary>Reserved; nothing reads it yet.</summary>
    public bool IsSystemAdmin { get; init; }

    /// <summary>Signed in and refused everything; the front end says so instead of failing on every request.</summary>
    public bool IsBlocked { get; init; }
}

/// <summary>A change on the users screen: the one flag it sets.</summary>
public sealed record UserFlagRequest(bool Value);

/// <summary>What a user sends to ask for more requests.</summary>
public sealed record QuotaRequestInput(string? Reason, string? Industry);

/// <summary>The stored request: its id, status and when it was last sent.</summary>
public sealed record QuotaRequestReceipt(long Id, string Status, DateTimeOffset SentAt);

/// <summary>An address to add to the allow list.</summary>
public sealed record GrantAdminRequest(string Email, string? DisplayName, string? Note);

/// <summary>
/// Signing in, and managing who may.
///
/// These are the only endpoints that require a signed-in caller. The aggregate itself
/// stays open: it is a published sanctions list, and putting a login in front of reading
/// it would protect nothing that is not already public on the publishers' own websites.
/// What needs protecting is the ability to hand out access, which is what lives here.
///
/// Mapped only when a sign-in has actually been configured — see
/// <see cref="SancMeldAuthExtensions.AddSancMeldGoogleAuth"/>.
/// </summary>
internal static class AdminEndpoints
{
    public static IEndpointRouteBuilder MapSancMeldAuth(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/auth").WithTags("Auth");

        group.MapGet("/config", (IOptions<SancMeldAuthOptions> options) =>
            TypedResults.Ok(new AuthConfiguration(options.Value.GoogleClientId)))
        .AllowAnonymous()
        .WithName("GetAuthConfiguration")
        .WithSummary("The OAuth client id a front end needs to start a Google sign-in.")
        .WithDescription(
            "Served rather than compiled into the front end so that both halves of a deployment " +
            "cannot disagree about which Google application they belong to. A client id is public " +
            "by construction — it is visible in the sign-in URL of every application that uses one — " +
            "and it is not what protects anything here; the token's signature and audience are.");

        group.MapGet("/me", async (
            ClaimsPrincipal user, AdminDirectory admins, ApiUserDirectory users, IOptions<SancMeldAuthOptions> options, CancellationToken ct) =>
        {
            var email = SancMeldClaims.EmailOf(user);

            if (string.IsNullOrEmpty(email))
            {
                // Authenticated, but the token carries no address to match on. Nothing to
                // report and nothing to grant against.
                return TypedResults.Ok(new SignedInUser(string.Empty, null, SancMeldClaims.SubjectOf(user), false));
            }

            var verified = SancMeldClaims.EmailIsVerified(user);
            // A key never administers, whoever owns it; saying otherwise would invite a 403.
            var admin = verified && !SancMeldClaims.IsApiKey(user) ? await admins.FindActiveAsync(email, ct) : null;

            // The first sign-in creates the user and their budget.
            var account = verified
                ? await users.EnsureAsync(email, SancMeldClaims.NameOf(user), SancMeldClaims.SubjectOf(user), options.Value.DefaultRequestLimit, ct)
                : null;

            return TypedResults.Ok(new SignedInUser(
                email, SancMeldClaims.NameOf(user), SancMeldClaims.SubjectOf(user), admin is not null)
            {
                CanWrite = verified && account?.IsBlocked != true,
                RequestLimit = account?.RequestLimit,
                RequestsUsed = account?.RequestsUsed,
                RequestsRemaining = account is null ? null : Math.Max(0, account.RequestLimit - account.RequestsUsed),
                PendingQuotaRequestAt = verified ? await users.PendingRequestAtAsync(email, ct) : null,
                CanSeeAdditionalInfo = account is { IsAllowedForAdditionalInfo: true, IsBlocked: false },
                IsUserAdmin = account is { IsUserAdmin: true, IsBlocked: false } && !SancMeldClaims.IsApiKey(user),
                IsSystemAdmin = account?.IsSystemAdmin ?? false,
                IsBlocked = account?.IsBlocked ?? false,
            });
        })
        .RequireAuthorization()
        .WithName("GetSignedInUser")
        .WithSummary("Who the token says you are, and whether this instance grants you anything.")
        .WithDescription(
            "A valid token and administrator access are two different questions, and this answers " +
            "both separately. Anybody with a Google account can obtain a token this endpoint accepts; " +
            "isAdmin is false for all of them until an existing administrator adds the address. A " +
            "front end uses that to show 'signed in, waiting for access' instead of a bare 403.");

        return endpoints;
    }

    /// <summary>
    /// Asking for a bigger request budget. Signed in, not metered: an exhausted budget is
    /// exactly when this has to work.
    /// </summary>
    public static IEndpointRouteBuilder MapQuotaRequests(this IEndpointRouteBuilder endpoints)
    {
        endpoints.MapPost("/quota-requests", async Task<Results<Ok<QuotaRequestReceipt>, BadRequest<string>, ForbidHttpResult>> (
            QuotaRequestInput input, ClaimsPrincipal user, ApiUserDirectory users, CancellationToken ct) =>
        {
            var email = SancMeldClaims.EmailOf(user);
            if (string.IsNullOrEmpty(email) || !SancMeldClaims.EmailIsVerified(user))
            {
                return TypedResults.Forbid();
            }

            var reason = input?.Reason?.Trim() ?? string.Empty;
            var industry = input?.Industry?.Trim() ?? string.Empty;

            if (reason.Length < 10)
            {
                return TypedResults.BadRequest("Say in a sentence why you need more requests.");
            }

            if (industry.Length == 0)
            {
                return TypedResults.BadRequest("Say which industry you work in.");
            }

            if (reason.Length > 2000 || industry.Length > 100)
            {
                return TypedResults.BadRequest("The reason is limited to 2 000 characters and the industry to 100.");
            }

            var stored = await users.RequestMoreAsync(email, reason, industry, ct);
            return TypedResults.Ok(new QuotaRequestReceipt(stored.Id, stored.Status, stored.UpdatedAt ?? stored.CreatedAt));
        })
        .RequireAuthorization()
        .WithTags("Auth")
        .WithName("RequestMoreRequests")
        .WithSummary("Ask for a bigger request budget: why, and in which industry — writes.")
        .WithDescription(
            "Stored under the caller's verified address; one open request per address, so sending " +
            "again updates it. The operator reads the requests in the quota_request table and raises " +
            "api_user.request_limit by hand. Not metered.");

        return endpoints;
    }

    /// <summary>
    /// The users screen: who signed in, how many requests each made, block and unblock,
    /// grant the additional information. Nothing here makes anyone an administrator.
    /// </summary>
    public static IEndpointRouteBuilder MapUsers(this IEndpointRouteBuilder endpoints)
    {
        var group = endpoints.MapGroup("/users")
            .WithTags("Auth")
            .RequireAuthorization(SancMeldPolicies.UserAdmin);

        group.MapGet("/", async (ApiUserDirectory users, CancellationToken ct) =>
            TypedResults.Ok(await users.ListAsync(ct)))
        .WithName("GetUsers")
        .WithSummary("Everyone who signed in, with their request counts, blocks and grants.")
        .WithDescription("User administrators only. requestsUsed counts checks, screenings and register lookups in total.");

        group.MapPost("/{id:guid}/blocked", async Task<Results<Ok<UserView>, NotFound, Conflict<string>>> (
            ApiUserDirectory users, Guid id, UserFlagRequest request, ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var by = SancMeldClaims.EmailOf(user) ?? "unknown";
            var (outcome, view) = await users.SetBlockedAsync(id, request.Value, by, ct);
            logs.CreateLogger("SancMeld.Users").LogInformation("{By} set blocked={Value} on {Email}: {Outcome}.", by, request.Value, view?.Email, outcome);

            return outcome switch
            {
                UserChangeOutcome.NotFound => TypedResults.NotFound(),
                UserChangeOutcome.AdminCannotBeBlocked => TypedResults.Conflict("An administrator cannot be blocked here; that is done in the database."),
                _ => TypedResults.Ok(view!),
            };
        })
        .WithName("SetUserBlocked")
        .WithSummary("Block or unblock a user — writes. Never an administrator.")
        .WithDescription(
            "A blocked user is refused every write, sees no additional information, and their API keys " +
            "stop working; reading the public lists stays open, as for anyone. Who blocked and when is kept.");

        group.MapPost("/{id:guid}/additional-info", async Task<Results<Ok<UserView>, NotFound>> (
            ApiUserDirectory users, Guid id, UserFlagRequest request, ClaimsPrincipal user, ILoggerFactory logs, CancellationToken ct) =>
        {
            var by = SancMeldClaims.EmailOf(user) ?? "unknown";
            var (outcome, view) = await users.SetAdditionalInfoAsync(id, request.Value, by, ct);
            logs.CreateLogger("SancMeld.Users").LogInformation("{By} set additional-info={Value} on {Email}.", by, request.Value, view?.Email);

            return outcome == UserChangeOutcome.NotFound ? TypedResults.NotFound() : TypedResults.Ok(view!);
        })
        .WithName("SetUserAdditionalInfo")
        .WithSummary("Grant or withdraw the additional information — writes.")
        .WithDescription(
            "Leads from the Slovak business register (name matches without a date of birth) and open-source " +
            "research. Who changed it last and when is kept on the user.");

        return endpoints;
    }

    public static IEndpointRouteBuilder MapAdmins(this IEndpointRouteBuilder endpoints)
    {
        // Every route in the group, including ones added later, requires the allow list.
        // Stated once here rather than per endpoint: forgetting it on a single route is
        // exactly the mistake that leaves an administrative endpoint open.
        var group = endpoints.MapGroup("/admins")
            .WithTags("Auth")
            .RequireAuthorization(SancMeldPolicies.Admin);

        group.MapGet("/", async (AdminDirectory admins, bool? includeRevoked, CancellationToken ct) =>
            TypedResults.Ok(await admins.ListAsync(includeRevoked ?? false, ct)))
        .WithName("GetAdmins")
        .WithSummary("Who may administer this instance.")
        .WithDescription(
            "Pass includeRevoked=true to see entries whose access was withdrawn, along with who " +
            "withdrew it and when. lastSeenAt is how an allow list gets pruned: an entry nobody " +
            "has used in a year is one nobody needs.");

        group.MapPost("/", async Task<Results<Ok<AdminView>, BadRequest<string>>> (
            AdminDirectory admins, GrantAdminRequest request, ClaimsPrincipal user, CancellationToken ct) =>
        {
            if (request is null || string.IsNullOrWhiteSpace(request.Email))
            {
                return TypedResults.BadRequest("Supply the Google address to grant access to.");
            }

            try
            {
                var granted = await admins.GrantAsync(
                    request.Email,
                    request.DisplayName,
                    request.Note,
                    SancMeldClaims.EmailOf(user) ?? "unknown",
                    ct);

                return TypedResults.Ok(granted);
            }
            catch (ArgumentException ex)
            {
                return TypedResults.BadRequest(ex.Message);
            }
        })
        .WithName("GrantAdmin")
        .WithSummary("Add a Google address to the allow list.")
        .WithDescription(
            "The address is stored lower-cased and matched exactly against the one in the token. " +
            "It is not checked against Google: an entry for an address that does not exist simply " +
            "never matches. Granting an address that was revoked restores it. Dots and +tags are " +
            "kept as typed — Gmail treats them as the same mailbox and most providers do not, and " +
            "guessing would mean admitting an address nobody granted.");

        group.MapDelete("/{id:guid}", async Task<Results<NoContent, NotFound, BadRequest<string>>> (
            AdminDirectory admins, Guid id, ClaimsPrincipal user, CancellationToken ct) =>
        {
            var caller = SancMeldClaims.EmailOf(user) ?? "unknown";

            // Revoking yourself is almost always a mistake and is never necessary: another
            // administrator can do it, which also leaves the trail pointing at a second person.
            var self = await admins.FindActiveAsync(caller, ct);

            if (self is not null && self.Id == id)
            {
                return TypedResults.BadRequest(
                    "You cannot revoke your own access. Ask another administrator to do it.");
            }

            return await admins.RevokeAsync(id, caller, ct) switch
            {
                RevokeOutcome.Revoked => TypedResults.NoContent(),
                RevokeOutcome.AlreadyRevoked => TypedResults.NoContent(),
                RevokeOutcome.LastActiveAdmin => TypedResults.BadRequest(
                    "This is the last active administrator. Revoking it would leave the admin API "
                    + "reachable only from a shell on the server; grant somebody else first."),
                _ => TypedResults.NotFound(),
            };
        })
        .WithName("RevokeAdmin")
        .WithSummary("Withdraw access.")
        .WithDescription(
            "The entry is kept and marked revoked, carrying who withdrew it and when, rather than " +
            "deleted. Refused for the last remaining administrator and for yourself.");

        return endpoints;
    }
}
