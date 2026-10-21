using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using System.Text.Encodings.Web;
using HybridMeld.Core.Access;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;

namespace HybridMeld.Api;

/// <summary>
/// Bound from "HybridMeld:Auth". Nothing secret: the API verifies Google's ID tokens
/// against Google's published keys and never talks to Google. The same client id the
/// front end uses for SancMeld and PepMeld, so one sign-in opens all three.
/// </summary>
public sealed class HybridMeldAuthOptions
{
    public const string SectionName = "HybridMeld:Auth";

    public string GoogleClientId { get; set; } = string.Empty;

    [Range(0, 300)]
    public int ClockSkewSeconds { get; set; } = 30;

    public bool GoogleEnabled => !string.IsNullOrWhiteSpace(GoogleClientId);
}

/// <summary>Every endpoint needs a caller with the grant; there is nothing to show an anonymous reader.</summary>
public static class HybridMeldPolicies
{
    public const string User = "HybridMeld.User";
}

/// <summary>Claim names as Google writes them in an ID token, and as the API-key handler writes them to match.</summary>
public static class HybridMeldClaims
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

/// <summary>Signs a program in by its API key, sent as <c>X-Api-Key</c>; the principal is the key's owner.</summary>
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
                new Claim(HybridMeldClaims.Email, record.OwnerEmail),
                new Claim(HybridMeldClaims.EmailVerified, "true"),
                new Claim(HybridMeldClaims.Subject, "apikey:" + record.Id),
                new Claim(HybridMeldClaims.Name, record.Name),
                new Claim(HybridMeldClaims.AuthenticationMethod, Method),
            ],
            SchemeName,
            HybridMeldClaims.Email,
            roleType: null);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }

    protected override Task HandleChallengeAsync(AuthenticationProperties properties)
    {
        Response.StatusCode = StatusCodes.Status401Unauthorized;
        Response.Headers.WWWAuthenticate = $"{SchemeName} realm=\"HybridMeld\"";
        return Task.CompletedTask;
    }
}

public static class HybridMeldAuthExtensions
{
    private const string GoogleIssuer = "https://accounts.google.com";
    private const string SelectorScheme = "HybridMeld";

    public static IServiceCollection AddHybridMeldAuth(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        var options = new HybridMeldAuthOptions();
        configuration.GetSection(HybridMeldAuthOptions.SectionName).Bind(options);

        services.AddOptions<HybridMeldAuthOptions>()
            .Bind(configuration.GetSection(HybridMeldAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .ValidateOnStart();

        services.AddScoped<ApiUserDirectory>();
        services.AddScoped<ApiKeyDirectory>();
        services.AddSingleton<HybridAccess>();
        services.AddScoped<IAuthorizationHandler, VerifiedUserHandler>();

        services.AddAuthorization(authorization =>
        {
            authorization.AddPolicy(HybridMeldPolicies.User, policy =>
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
                    NameClaimType = HybridMeldClaims.Email,
                };
            });
        }

        return services;
    }
}

internal sealed class VerifiedUserRequirement : IAuthorizationRequirement;

/// <summary>A verified address with the grant on its SancMeld account and no block here. Against the tables on every request, so a withdrawal takes effect now.</summary>
internal sealed class VerifiedUserHandler(
    ApiUserDirectory users,
    HybridAccess access,
    ILogger<VerifiedUserHandler> logger) : AuthorizationHandler<VerifiedUserRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, VerifiedUserRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);

        var email = HybridMeldClaims.EmailOf(context.User);
        if (string.IsNullOrEmpty(email))
        {
            return;
        }

        if (!HybridMeldClaims.EmailIsVerified(context.User))
        {
            logger.LogWarning("Rejected {Email}: the provider does not state the address is verified.", email);
            return;
        }

        var user = await users.EnsureAsync(email, HybridMeldClaims.NameOf(context.User), HybridMeldClaims.SubjectOf(context.User)).ConfigureAwait(false);
        if (user.IsBlocked)
        {
            logger.LogWarning("Rejected {Email}: blocked by {BlockedBy} on {BlockedAt}.", email, user.BlockedBy, user.BlockedAt);
            return;
        }

        if (!await access.IsAllowedAsync(email, CancellationToken.None).ConfigureAwait(false))
        {
            logger.LogWarning("Rejected {Email}: the hybrid-threats workspace is not granted to this account in SancMeld.", email);
            return;
        }

        context.Succeed(requirement);
    }
}

/// <summary>The body of every error answer: a stable code and a sentence.</summary>
public sealed record ApiError(string Error, string Message);
