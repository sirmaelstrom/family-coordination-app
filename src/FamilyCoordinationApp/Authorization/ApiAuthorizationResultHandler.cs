using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Policy;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// Gives an /api policy refusal its reason. When a signed-in caller is forbidden on <c>/api</c> and a handler failed
/// with a message (<see cref="SiteAdminHandler.FailureMessage"/>), the 403 carries that message as its
/// <c>{ message }</c>. Every other outcome (success, a challenge, a forbid with no reason, any non-/api path) goes
/// to the framework's default handler unchanged, so the cookie scheme's events and the /api status-code backfill
/// behave as before.
/// </summary>
public sealed class ApiAuthorizationResultHandler : IAuthorizationMiddlewareResultHandler
{
    private readonly AuthorizationMiddlewareResultHandler _default = new();

    public Task HandleAsync(RequestDelegate next, HttpContext context, AuthorizationPolicy policy, PolicyAuthorizationResult authorizeResult)
    {
        if (authorizeResult.Forbidden
            && context.Request.Path.StartsWithSegments(ApiAwareAuthEvents.ApiPrefix)
            && authorizeResult.AuthorizationFailure?.FailureReasons
                .Select(r => r.Message)
                .FirstOrDefault(m => !string.IsNullOrWhiteSpace(m)) is { } message)
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return context.Response.WriteAsJsonAsync(new { message });
        }

        return _default.HandleAsync(next, context, policy, authorizeResult);
    }
}
