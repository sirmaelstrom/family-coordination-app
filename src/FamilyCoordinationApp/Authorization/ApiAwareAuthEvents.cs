using Microsoft.AspNetCore.Authentication;

namespace FamilyCoordinationApp.Authorization;

/// <summary>
/// Auth-scheme redirect events made SPA-aware (de-Blazor WP-03). The de-Blazored SvelteKit shell boots
/// anonymously and calls <c>GET /api/me</c>; an expired/absent cookie on any <c>/api</c> call must surface as a
/// <b>401/403 status</b>, NOT the default 302 redirect (to the Google OAuth endpoint or the cookie login /
/// access-denied page). A 302 is transparently followed by <c>fetch</c> and returns HTML the SPA cannot
/// distinguish from success, so the session store could never detect "not logged in".
///
/// <para><b>Load-bearing scheme note:</b> the app's <c>DefaultChallengeScheme</c> is <b>Google</b> (Program.cs),
/// so an anonymous <c>RequireAuthorization</c> failure challenges the Google handler — the 401 for an anonymous
/// <c>/api</c> request therefore comes from Google's <c>OnRedirectToAuthorizationEndpoint</c>, NOT the cookie's
/// <c>OnRedirectToLogin</c>. The forbid (403) path falls back to the cookie scheme's
/// <c>OnRedirectToAccessDenied</c>. All three are wired to this one helper so the behavior is identical
/// regardless of which scheme handles the redirect.</para>
///
/// <para>Additive and <c>/api</c>-scoped: browser page routes keep redirecting; the OAuth challenge and the
/// cookie itself are unchanged (MN7).</para>
/// </summary>
public static class ApiAwareAuthEvents
{
    /// <summary>The path prefix whose auth redirects are converted to bare status codes for the SPA.</summary>
    public static readonly PathString ApiPrefix = "/api";

    /// <summary>
    /// If the request targets <c>/api</c>, write <paramref name="apiStatusCode"/> and suppress the redirect;
    /// otherwise perform the normal redirect. Works for any auth scheme's <see cref="RedirectContext{TOptions}"/>
    /// (cookie login / access-denied and the Google authorization-endpoint challenge all share this shape).
    /// </summary>
    public static Task StatusForApiElseRedirect<TOptions>(RedirectContext<TOptions> context, int apiStatusCode)
        where TOptions : AuthenticationSchemeOptions
    {
        if (context.Request.Path.StartsWithSegments(ApiPrefix))
        {
            context.Response.StatusCode = apiStatusCode;
            // The body must be non-empty: UseStatusCodePagesWithReExecute re-executes any empty-body 4xx
            // through the GET-only /not-found page with the ORIGINAL method, so a non-GET /api auth failure
            // would surface as 405 instead of 401/403 (CORRECTIONS: fca-empty-404-surfaces-as-405-on-delete).
            return context.Response.WriteAsJsonAsync(new
            {
                message = apiStatusCode == StatusCodes.Status403Forbidden ? "Forbidden" : "Unauthorized",
            });
        }

        context.Response.Redirect(context.RedirectUri);
        return Task.CompletedTask;
    }
}
