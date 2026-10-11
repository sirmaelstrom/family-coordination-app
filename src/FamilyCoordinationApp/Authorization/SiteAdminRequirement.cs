using System.Security.Claims;
using FamilyCoordinationApp.Services;
using Microsoft.AspNetCore.Authorization;

namespace FamilyCoordinationApp.Authorization;

/// <summary>The named authorization policies (quest 82fc7f54).</summary>
public static class AuthorizationPolicies
{
    /// <summary>
    /// Signed in, whitelisted, and listed in <c>SITE_ADMIN_EMAILS</c>. The one site-admin access gate: it guards the
    /// global household-request and household-create groups in <c>SettingsAdminEndpoints</c>.
    /// </summary>
    public const string SiteAdmin = "SiteAdmin";
}

/// <summary>The caller must be a site admin (<see cref="ISiteAdminService"/>).</summary>
public sealed class SiteAdminRequirement : IAuthorizationRequirement;

/// <summary>
/// Succeeds <see cref="SiteAdminRequirement"/> for a site admin. Otherwise it fails with
/// <see cref="FailureMessage"/>, which <see cref="ApiAuthorizationResultHandler"/> writes as the 403's
/// <c>{ message }</c> on <c>/api</c>.
/// </summary>
public sealed class SiteAdminHandler(ISiteAdminService siteAdmin) : AuthorizationHandler<SiteAdminRequirement>
{
    public const string FailureMessage = "Site admin access required.";

    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, SiteAdminRequirement requirement)
    {
        if (siteAdmin.IsSiteAdmin(context.User))
        {
            context.Succeed(requirement);
        }
        else
        {
            context.Fail(new AuthorizationFailureReason(this, FailureMessage));
        }

        return Task.CompletedTask;
    }
}

/// <summary>
/// The one way to ask "is this principal a site admin": the claim email checked against <see cref="ISiteAdminService"/>.
/// The <see cref="AuthorizationPolicies.SiteAdmin"/> handler uses it as an access gate; the feedback list and
/// <c>/api/me</c> use it as a data signal (what the caller sees), never to admit or refuse a request.
/// </summary>
public static class SiteAdminPrincipalExtensions
{
    public static bool IsSiteAdmin(this ISiteAdminService siteAdmin, ClaimsPrincipal principal) =>
        siteAdmin.IsSiteAdmin(principal.FindFirst(ClaimTypes.Email)?.Value);
}
