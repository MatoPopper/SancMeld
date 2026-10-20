using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using PepMeld.Core.Access;

namespace PepMeld.Api;

/// <summary>
/// Bound from "PepMeld:Auth". Nothing secret: the API verifies Google's ID tokens against
/// Google's published keys and never talks to Google, so no client secret exists. The
/// client id differs per deployment, which is why it is configuration.
/// </summary>
public sealed class PepMeldAuthOptions
{
    public const string SectionName = "PepMeld:Auth";

    /// <summary>
    /// The OAuth client id, "1234-abcd.apps.googleusercontent.com". Empty means no
    /// sign-in with Google; API keys still work, and are the only way in. The front end,
    /// when it is connected, signs in with the same client id SancMeld uses.
    /// </summary>
    public string GoogleClientId { get; set; } = string.Empty;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;

    /// <summary>Checks a newly seen address may start in total. Raised per user in api_user, with the CLI.</summary>
    [Range(0, int.MaxValue)]
    public int DefaultRequestLimit { get; set; } = ApiUserDirectory.DefaultLimit;

    public bool GoogleEnabled => !string.IsNullOrWhiteSpace(GoogleClientId);
}

/// <summary>
/// Unlike SancMeld, every endpoint that touches a check requires a caller: a check
/// belongs to someone, is counted against their budget, and its result is theirs to
/// read. There is nothing to serve an anonymous caller.
/// </summary>
public static class PepMeldPolicies
{
    /// <summary>A signed-in caller with a verified address who is not blocked. Needed to start and read checks.</summary>
    public const string User = "PepMeld.User";
}

/// <summary>Claim names as Google writes them in an ID token, and as the API-key handler writes them to match.</summary>
public static class PepMeldClaims
{
    public const string Email = "email";
    public const string EmailVerified = "email_verified";
    public const string Subject = "sub";
    public const string Name = "name";
    public const string AuthenticationMethod = "amr";

    public static bool IsApiKey(ClaimsPrincipal? user) =>
        user?.FindFirstValue(AuthenticationMethod) == ApiKeyAuthenticationHandler.Method;

    public static string? EmailOf(ClaimsPrincipal? user) =>
        user?.FindFirstValue(Email) ?? user?.FindFirstValue(ClaimTypes.Email);

    public static bool EmailIsVerified(ClaimsPrincipal? user) =>
        string.Equals(user?.FindFirstValue(EmailVerified), "true", StringComparison.OrdinalIgnoreCase);

    public static string? SubjectOf(ClaimsPrincipal? user) =>
        user?.FindFirstValue(Subject) ?? user?.FindFirstValue(ClaimTypes.NameIdentifier);

    public static string? NameOf(ClaimsPrincipal? user) => user?.FindFirstValue(Name);
}

/// <summary>
/// Signs a program in by its API key, sent as <c>X-Api-Key</c>. The principal is the
/// key's owner, with the address marked verified: the operator who made the key vouched
/// for it.
/// </summary>
internal sealed class ApiKeyAuthenticationHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder)
    : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string SchemeName = "ApiKey";
    public const string HeaderName = "X-Api-Key";
    public const string Method = "api_key";

    protected override async Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (!Request.Headers.TryGetValue(HeaderName, out var values) || values.ToString() is not { Length: > 0 } key)
        {
            return AuthenticateResult.NoResult();
        }

        var keys = Context.RequestServices.GetRequiredService<ApiKeyDirectory>();
        var record = await keys.FindAsync(key, Context.RequestAborted).ConfigureAwait(false);

        if (record is null)
        {
            Logger.LogWarning("Rejected an API key that is unknown or revoked.");
            return AuthenticateResult.Fail("Unknown or revoked API key.");
        }

        var identity = new ClaimsIdentity(
            [
                new Claim(PepMeldClaims.Email, record.OwnerEmail),
                new Claim(PepMeldClaims.EmailVerified, "true"),
                new Claim(PepMeldClaims.Subject, "apikey:" + record.Id),
                new Claim(PepMeldClaims.Name, record.Name),
                new Claim(PepMeldClaims.AuthenticationMethod, Method),
            ],
            SchemeName,
            PepMeldClaims.Email,
            roleType: null);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} realm=\"PepMeld\"";
        return Task.CompletedTask;
    }
}

public static class PepMeldAuthExtensions
{
    private const string GoogleIssuer = "https://accounts.google.com";
    private const string SelectorScheme = "PepMeld";

    /// <summary>
    /// Always the API-key scheme; Google ID tokens as well when a client id is configured.
    /// One default scheme looks at the request: an X-Api-Key header is a program with a
    /// key, anything else a person with a Google ID token.
    /// </summary>
    public static IServiceCollection AddPepMeldAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new PepMeldAuthOptions();
        configuration.GetSection(PepMeldAuthOptions.SectionName).Bind(options);

        services.AddOptions<PepMeldAuthOptions>()
            .Bind(configuration.GetSection(PepMeldAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ApiUserDirectory>();
        services.AddScoped<ApiKeyDirectory>();
        services.AddScoped<IAuthorizationHandler, VerifiedUserHandler>();

        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(PepMeldPolicies.User, policy =>
            {
                policy.RequireAuthenticatedUser();
                policy.AddRequirements(new VerifiedUserRequirement());
            });
        });

        var authentication = services.AddAuthentication(SelectorScheme)
            .AddPolicyScheme(SelectorScheme, "Google ID token or API key", selector =>
            {
                selector.ForwardDefaultSelector = context =>
                    options.GoogleEnabled && !context.Request.Headers.ContainsKey(ApiKeyAuthenticationHandler.HeaderName)
                        ? JwtBearerDefaults.AuthenticationScheme
                        : ApiKeyAuthenticationHandler.SchemeName;
            })
            .AddScheme<AuthenticationSchemeOptions, ApiKeyAuthenticationHandler>(ApiKeyAuthenticationHandler.SchemeName, null);

        if (options.GoogleEnabled)
        {
            authentication.AddJwtBearer(jwt =>
            {
                jwt.Authority = GoogleIssuer;
                jwt.Audience = options.GoogleClientId;
                jwt.MapInboundClaims = false;
                jwt.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidIssuers = [GoogleIssuer, "accounts.google.com"],
                    ValidAudience = options.GoogleClientId,
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateLifetime = true,
                    ValidateIssuerSigningKey = true,
                    ClockSkew = TimeSpan.FromSeconds(options.ClockSkewSeconds),
                    NameClaimType = PepMeldClaims.Email,
                };
            });
        }

        return services;
    }
}

internal sealed class VerifiedUserRequirement : IAuthorizationRequirement;

/// <summary>
/// Lets a verified address in and makes sure it has a row, and with it a budget, in
/// api_user. Against the table on every request: blocking someone has to take effect
/// now, not when their token expires.
/// </summary>
internal sealed class VerifiedUserHandler(
    ApiUserDirectory users,
    IOptions<PepMeldAuthOptions> options,
    ILogger<VerifiedUserHandler> logger) : AuthorizationHandler<VerifiedUserRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, VerifiedUserRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var email = PepMeldClaims.EmailOf(context.User);
        if (string.IsNullOrEmpty(email))
        {
            return;
        }

        if (!PepMeldClaims.EmailIsVerified(context.User))
        {
            logger.LogWarning("Rejected {Email}: the provider does not state the address is verified.", email);
            return;
        }

        var user = await users.EnsureAsync(email, PepMeldClaims.NameOf(context.User), PepMeldClaims.SubjectOf(context.User),
            options.Value.DefaultRequestLimit).ConfigureAwait(false);

        if (user.IsBlocked)
        {
            logger.LogWarning("Rejected {Email}: blocked by {BlockedBy} on {BlockedAt}.", email, user.BlockedBy, user.BlockedAt);
            return;
        }

        context.Succeed(requirement);
    }
}

/// <summary>
/// Spends one request of the caller's budget before the endpoint runs, and answers 429
/// once it is gone. Only on POST /checks: reading a result one already paid for is free.
/// </summary>
public sealed class RequestBudgetFilter : IEndpointFilter
{
    public async ValueTask<object?> InvokeAsync(EndpointFilterInvocationContext context, EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var http = context.HttpContext;
        var email = PepMeldClaims.EmailOf(http.User);
        if (string.IsNullOrEmpty(email))
        {
            return Results.Unauthorized();
        }

        var users = http.RequestServices.GetRequiredService<ApiUserDirectory>();
        var state = await users.TryConsumeAsync(email, http.RequestAborted).ConfigureAwait(false);

        http.Response.Headers["X-Request-Limit"] = state.Limit.ToString(CultureInfo.InvariantCulture);
        http.Response.Headers["X-Requests-Remaining"] = state.Remaining.ToString(CultureInfo.InvariantCulture);

        if (!state.Allowed)
        {
            return Results.Json(new ApiError(
                "request_limit_reached",
                $"You have used all {state.Limit} checks of your account. Ask the operator for a bigger budget."),
                statusCode: StatusCodes.Status429TooManyRequests);
        }

        return await next(context).ConfigureAwait(false);
    }
}

/// <summary>The body of every error answer: a stable code and a sentence.</summary>
public sealed record ApiError(string Error, string Message, Guid? CheckId = null);
