using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Options;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// The login gate for <c>/api</c> (quest 82fc7f54), installed as the <c>FallbackPolicy</c>. The fallback policy is
/// what the authorization middleware evaluates for an endpoint that carries no authorization metadata of its own, so
/// an /api route group that never calls <c>RequireAuthorization()</c> is still refused for an anonymous or
/// non-whitelisted caller. A route that must stay public says so with <c>AllowAnonymous()</c>.
/// </summary>
public sealed class ApiLoginGateRequirement : IAuthorizationRequirement;

/// <summary>
/// Scopes the fallback policy to <c>/api</c>. Off <c>/api</c> it succeeds without I/O, so Razor Pages, static files
/// and the SPA fallback answer exactly as they did with no fallback policy. On <c>/api</c> it succeeds only when the
/// app's <c>DefaultPolicy</c> (signed in + whitelisted, the policy a bare <c>RequireAuthorization()</c> applies) does.
/// <para>When the resource isn't the request's <see cref="HttpContext"/> it cannot tell the path, so it fails closed.
/// The authorization middleware always passes the <see cref="HttpContext"/>.</para>
/// </summary>
public sealed class ApiLoginGateHandler : AuthorizationHandler<ApiLoginGateRequirement>
{
    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, ApiLoginGateRequirement requirement)
    {
        if (context.Resource is not HttpContext http)
        {
            return;
        }

        if (!http.Request.Path.StartsWithSegments(ApiAwareAuthEvents.ApiPrefix))
        {
            context.Succeed(requirement);
            return;
        }

        // Resolved per request, not injected: IAuthorizationService is built over every IAuthorizationHandler,
        // this one included, so a constructor dependency on it would be circular.
        var services = http.RequestServices;
        var defaultPolicy = services.GetRequiredService<IOptions<AuthorizationOptions>>().Value.DefaultPolicy;
        var result = await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(context.User, http, defaultPolicy);
        if (result.Succeeded)
        {
            context.Succeed(requirement);
        }
    }
}
