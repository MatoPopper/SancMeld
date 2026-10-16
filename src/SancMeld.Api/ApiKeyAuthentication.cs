using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using SancMeld.Core.Access;

namespace SancMeld.Api;

/// <summary>
/// Signs a program in by its API key, sent as <c>X-Api-Key</c>. The principal it makes
/// is the key's owner, with the address marked verified — the operator who made the key
/// vouched for it — and an <c>amr</c> claim saying it came from a key, which keeps it
/// off the admin endpoints.
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
                new Claim(SancMeldClaims.Email, record.OwnerEmail),
                new Claim(SancMeldClaims.EmailVerified, "true"),
                new Claim(SancMeldClaims.Subject, "apikey:" + record.Id),
                new Claim(SancMeldClaims.Name, record.Name),
                new Claim(SancMeldClaims.AuthenticationMethod, Method),
            ],
            SchemeName,
            SancMeldClaims.Email,
            roleType: null);

        return AuthenticateResult.Success(new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName));
    }
}
