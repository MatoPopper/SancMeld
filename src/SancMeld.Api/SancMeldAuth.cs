using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.IdentityModel.Tokens;
using SancMeld.Core.Access;

namespace SancMeld.Api;

/// <summary>
/// Configuration for the sign-in, bound from "SancMeld:Auth".
///
/// There is deliberately nothing secret in here, and that is the whole point of
/// validating Google's ID token rather than running the OAuth code flow ourselves: the
/// code flow needs a client secret, and a client secret needs somewhere to live that is
/// not this repository. A resource server that only verifies signatures needs no secret
/// at all, so the public repository stays complete and nothing has to be kept out of it.
///
/// The client id is not a secret either — it is compiled into every front end and
/// travels in the browser's address bar — but it is still configuration rather than a
/// committed value, because it differs per deployment.
/// </summary>
public sealed class SancMeldAuthOptions
{
    public const string SectionName = "SancMeld:Auth";

    /// <summary>
    /// The OAuth client id from Google Cloud Console, of the form
    /// "1234-abcd.apps.googleusercontent.com".
    ///
    /// Empty means no sign-in: the host stays entirely unauthenticated, which is the
    /// documented default for the open-source edition, and the admin endpoints are not
    /// mapped at all rather than being mapped and unreachable.
    /// </summary>
    public string GoogleClientId { get; set; } = string.Empty;

    /// <summary>
    /// Tokens issued more than this long ago are rejected regardless of their own expiry.
    /// Google's ID tokens live an hour; nothing here needs to accept a stale one.
    /// </summary>
    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>
    /// Requests a newly seen address may make in total (checks, screenings, register
    /// lookups). Raised per user in the api_user table, by hand.
    /// </summary>
    [Range(0, int.MaxValue)]
    public int DefaultRequestLimit { get; set; } = ApiUserDirectory.DefaultLimit;

    /// <summary>
    /// Whom a user who has used up their budget should write to. Configuration, not code:
    /// it is a person's address and this repository is public. Empty means the message
    /// tells them to contact the operator of the instance.
    /// </summary>
    public string QuotaContactEmail { get; set; } = string.Empty;

    public bool IsEnabled => !string.IsNullOrWhiteSpace(GoogleClientId);
}

/// <summary>
/// Authorisation policies this library defines.
///
/// Two names, one allow list behind both today. They are separate because the questions
/// are separate — recording a screening decision is what an analyst does all day, handing
/// out access is not — and because the day a role column arrives, only one of them
/// changes. Collapsing them into a single "admin" would mean the first analyst added to
/// the instance can also revoke everybody else.
/// </summary>
public static class SancMeldPolicies
{
    /// <summary>The caller is signed in AND present on the allow list in the admin table.</summary>
    public const string Admin = "SancMeld.Admin";

    /// <summary>
    /// May write: screen a name, record an adjudication. Since 2026-10-16 any signed-in
    /// caller with a verified address; what limits them is the request budget in
    /// api_user, enforced per endpoint by <see cref="RequestBudgetFilter"/>.
    ///
    /// Reads are deliberately not covered. The aggregate is a published sanctions list;
    /// what this protects is the rows a caller can create in this database and the
    /// decisions attributed to them.
    /// </summary>
    public const string Write = "SancMeld.Write";
}

/// <summary>
/// Present in the container when some authentication scheme has been configured.
///
/// <see cref="SancMeldExtensions.MapSancMeld"/> looks for it and only maps the endpoints
/// that require a signed-in caller when it is there. Without it those endpoints would be
/// mapped against a scheme that does not exist and every call would fail with a 500
/// rather than the 404 that honestly describes a host with no sign-in.
/// </summary>
public sealed class SancMeldAuthMarker;

/// <summary>
/// Wiring the sign-in.
///
/// Two methods on purpose. The allow list and the policy that reads it are independent of
/// how the caller proved who they are, so the hosted edition can register its own OIDC
/// handler — or an entirely different notion of identity — and still reuse the rest.
/// </summary>
public static class SancMeldAuthExtensions
{
    /// <summary>
    /// Registers the allow list and the <see cref="SancMeldPolicies.Admin"/> policy,
    /// without saying anything about how a caller is authenticated.
    ///
    /// Call this if you bring your own authentication scheme. Whatever it is, it has to
    /// produce an "email" claim, because that is what the allow list is keyed on.
    /// </summary>
    public static IServiceCollection AddSancMeldAdminAccess(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddScoped<AdminDirectory>();
        services.AddScoped<ApiUserDirectory>();
        services.AddScoped<IAuthorizationHandler, VerifiedAddressHandler>();

        // Scoped, not singleton: it queries the database on every authorisation check and
        // needs the request's DbContext. ASP.NET resolves handlers from request services.
        services.AddScoped<IAuthorizationHandler, AdminRequirementHandler>();

        services.AddAuthorization(options =>
        {
            options.AddPolicy(SancMeldPolicies.Admin, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new AllowListedRequirement());
            });

            options.AddPolicy(SancMeldPolicies.Write, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new VerifiedAddressRequirement());
            });
        });

        services.AddSingleton<SancMeldAuthMarker>();
        return services;
    }

    /// <summary>
    /// Accepts Google ID tokens as bearer tokens, and nothing else.
    ///
    /// The API never talks to Google. It fetches the published signing keys, verifies the
    /// signature, the issuer, the audience and the expiry, and that is the entire
    /// exchange — which is why no client secret exists anywhere in this system. Obtaining
    /// the token is the client's job: a browser front end does it with Google Identity
    /// Services, which needs only the client id.
    ///
    /// Returns whether sign-in was actually configured, so the host knows whether to add
    /// the authentication middleware.
    /// </summary>
    public static bool AddSancMeldGoogleAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new SancMeldAuthOptions();
        configuration.GetSection(SancMeldAuthOptions.SectionName).Bind(options);

        services.AddOptions<SancMeldAuthOptions>()
            .Bind(configuration.GetSection(SancMeldAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        if (!options.IsEnabled)
        {
            return false;
        }

        services.AddSancMeldAdminAccess();

        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(jwt =>
            {
                // Discovery, and with it the signing keys, comes from Google and is
                // refreshed on its own schedule. Nothing about the keys is configured
                // here, so a key rotation is not an outage waiting to happen.
                jwt.Authority = GoogleIssuer;
                jwt.Audience = options.GoogleClientId;

                // Claims are used exactly as Google spells them: "sub", "email",
                // "email_verified". The default mapping renames them to SOAP-era URIs,
                // which turns every claim lookup into a lookup of two possible names.
                jwt.MapInboundClaims = false;

                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    // Google has issued both spellings over the years and still documents
                    // both as acceptable.
                    ValidIssuers = [GoogleIssuer, "accounts.google.com"],

                    // The audience is what stops a token minted for somebody else's
                    // application from working here. Without it any Google ID token in
                    // existence would be accepted.
                    ValidAudience = options.GoogleClientId,

                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
                    NameClaimType = SancMeldClaims.Email,
                };
            });

        return true;
    }

    private const string GoogleIssuer = "https://accounts.google.com";
}

/// <summary>Claim names as Google writes them in an ID token.</summary>
public static class SancMeldClaims
{
    public const string Email = "email";
    public const string EmailVerified = "email_verified";
    public const string Subject = "sub";
    public const string Name = "name";

    /// <summary>
    /// The caller's address, or null.
    ///
    /// Both spellings are checked because the raw claim is what Google sends and the
    /// long URI is what .NET produces when claim mapping is left on — this library
    /// turns mapping off, but a host bringing its own scheme may not have.
    /// </summary>
    public static string? EmailOf(ClaimsPrincipal? user) =>
        user?.FindFirstValue(Email) ?? user?.FindFirstValue(ClaimTypes.Email);

    /// <summary>
    /// Whether Google states the address itself is verified.
    ///
    /// It matters more than it looks. Some accounts carry an address the provider has
    /// never confirmed, and treating one of those as proof of identity would mean an
    /// allow list that can be matched by claiming somebody else's address.
    /// </summary>
    public static bool EmailIsVerified(ClaimsPrincipal? user) =>
        string.Equals(user?.FindFirstValue(EmailVerified), "true", StringComparison.OrdinalIgnoreCase);

    public static string? SubjectOf(ClaimsPrincipal? user) =>
        user?.FindFirstValue(Subject) ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? NameOf(ClaimsPrincipal? user) =>
        user?.FindFirstValue(Name);
}

/// <summary>Being on the allow list. The admin policy.</summary>
internal sealed class AllowListedRequirement : IAuthorizationRequirement;

/// <summary>A signed-in caller whose provider states the address is verified. The write policy.</summary>
internal sealed class VerifiedAddressRequirement : IAuthorizationRequirement;

/// <summary>
/// Lets any verified address write, and makes sure it has a row — and with it a budget —
/// in api_user. An unverified address could be somebody else's, and a budget kept under
/// it could be spent by them.
/// </summary>
internal sealed class VerifiedAddressHandler(
    ApiUserDirectory users,
    IOptions<SancMeldAuthOptions> options,
    ILogger<VerifiedAddressHandler> logger) : AuthorizationHandler<VerifiedAddressRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, VerifiedAddressRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var email = SancMeldClaims.EmailOf(context.User);
        if (string.IsNullOrEmpty(email))
        {
            return;
        }

        if (!SancMeldClaims.EmailIsVerified(context.User))
        {
            logger.LogWarning("Rejected {Email}: the provider does not state the address is verified.", email);
            return;
        }

        await users.EnsureAsync(email, SancMeldClaims.NameOf(context.User), SancMeldClaims.SubjectOf(context.User),
            options.Value.DefaultRequestLimit).ConfigureAwait(false);
        context.Succeed(requirement);
    }
}

/// <summary>
/// Spends one request of the caller's budget before the endpoint runs, and answers 429
/// with who to ask once it is gone. On a host without sign-in there are no users and no
/// budgets, and the filter lets everything through.
/// </summary>
public sealed class RequestBudgetFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var email = SancMeldClaims.EmailOf(http.User);

        if (http.RequestServices.GetService<SancMeldAuthMarker>() is null || string.IsNullOrEmpty(email))
        {
            return await next(context).ConfigureAwait(false);
        }

        var users = http.RequestServices.GetRequiredService<ApiUserDirectory>();
        var options = http.RequestServices.GetRequiredService<IOptions<SancMeldAuthOptions>>().Value;
        var state = await users.TryConsumeAsync(email, http.RequestAborted).ConfigureAwait(false);

        http.Response.Headers["X-Request-Limit"] = state.Limit.ToString(System.Globalization.CultureInfo.InvariantCulture);
        http.Response.Headers["X-Requests-Remaining"] = state.Remaining.ToString(System.Globalization.CultureInfo.InvariantCulture);

        if (!state.Allowed)
        {
            var contact = string.IsNullOrWhiteSpace(options.QuotaContactEmail) ? null : options.QuotaContactEmail;
            return Results.Json(new QuotaExceeded(
                "request_limit_reached",
                contact is null
                    ? $"You have used all {state.Limit} requests of your account. Ask the operator of this instance to raise the limit."
                    : $"You have used all {state.Limit} requests of your account. Write to {contact} to ask for more.",
                state.Limit, state.Used, contact), statusCode: StatusCodes.Status429TooManyRequests);
        }

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>The body of a 429: what happened, the budget, and whom to ask.</summary>
public sealed record QuotaExceeded(string Error, string Message, int Limit, int Used, string? Contact);

/// <summary>
/// Resolves the requirement against the admin table on every request.
///
/// Against the table, not against a claim baked into the token: revoking access has to
/// take effect now, and a Google ID token stays valid for an hour after it is issued. The
/// query is a single indexed lookup on a table with a handful of rows.
/// </summary>
internal sealed class AdminRequirementHandler(
    AdminDirectory admins,
    ILogger<AdminRequirementHandler> logger) : AuthorizationHandler<AllowListedRequirement>
{
    protected override async Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        AllowListedRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var email = SancMeldClaims.EmailOf(context.User);

        if (string.IsNullOrEmpty(email))
        {
            return;
        }

        if (!SancMeldClaims.EmailIsVerified(context.User))
        {
            logger.LogWarning("Rejected {Email}: the provider does not state the address is verified.", email);
            return;
        }

        var admin = await admins.FindActiveAsync(email).ConfigureAwait(false);

        if (admin is null)
        {
            // Worth a log line: a valid Google token from somebody nobody granted access
            // to is either a colleague waiting to be added or somebody trying the door.
            logger.LogWarning("Rejected {Email}: signed in, but not on the admin allow list.", email);
            return;
        }

        await admins.RecordSignInAsync(admin.Id, SancMeldClaims.SubjectOf(context.User)).ConfigureAwait(false);
        context.Succeed(requirement);
    }
}
