using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using SancMeld.Core.Access;

namespace SancMeld.Api;

/// <summary>
/// The additional information is for signed-in users whose row in api_user says so, and
/// for an API key whose owner's row says so — a key acts as its owner, here as for the
/// budget. A blocked user sees none of it. Asked once per request.
///
/// Outside a request — the CLI, a scheduled job — there is no caller to hide anything
/// from: whoever runs the console is the operator, and a job writes, it does not show.
/// On a host without sign-in every request is anonymous and sees none of it.
/// </summary>
internal sealed class HttpAdditionalInfoAccess(IHttpContextAccessor http, IServiceProvider services) : IAdditionalInfoAccess
{
    private bool? _allowed;

    public async ValueTask<bool> AllowedAsync(CancellationToken ct = default)
    {
        if (_allowed is { } known)
        {
            return known;
        }

        var context = http.HttpContext;
        if (context is null)
        {
            return (_allowed = true).Value;
        }

        var email = SancMeldClaims.EmailOf(context.User);
        if (context.RequestServices.GetService<SancMeldAuthMarker>() is null
            || context.User.Identity?.IsAuthenticated != true
            || string.IsNullOrEmpty(email)
            || !SancMeldClaims.EmailIsVerified(context.User))
        {
            return (_allowed = false).Value;
        }

        var users = services.GetRequiredService<ApiUserDirectory>();
        return (_allowed = await users.IsAllowedForAdditionalInfoAsync(email, ct).ConfigureAwait(false)).Value;
    }
}
