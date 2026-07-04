using FamilyCoordinationApp.Authorization;
using FluentAssertions;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;

namespace FamilyCoordinationApp.Tests.Authorization;

/// <summary>
/// WP-03 unit tests for <see cref="ApiAwareAuthEvents"/> (de-Blazor): the shared redirect event that surfaces
/// auth failures on <c>/api</c> as bare status codes (so the SPA session store can detect "not logged in")
/// while leaving browser page routes redirecting. Docker-free — the event is invoked directly on a constructed
/// <see cref="RedirectContext{TOptions}"/>.
/// </summary>
public sealed class ApiAwareAuthEventsTests
{
    private static RedirectContext<CookieAuthenticationOptions> RedirectCtx(string path)
    {
        var http = new DefaultHttpContext();
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();
        var scheme = new AuthenticationScheme(
            CookieAuthenticationDefaults.AuthenticationScheme, null, typeof(CookieAuthenticationHandler));
        return new RedirectContext<CookieAuthenticationOptions>(
            http, scheme, new CookieAuthenticationOptions(), new AuthenticationProperties(), "/account/login");
    }

    [Theory]
    [InlineData("/api/me")]
    [InlineData("/api/chores/board")]
    public async Task ApiPath_WritesStatus_AndDoesNotRedirect(string path)
    {
        var ctx = RedirectCtx(path);

        await ApiAwareAuthEvents.StatusForApiElseRedirect(ctx, StatusCodes.Status401Unauthorized);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status401Unauthorized);
        ctx.Response.Headers.ContainsKey("Location").Should().BeFalse("an /api auth failure must not 302-redirect");
        ctx.Response.Body.Length.Should().BeGreaterThan(0,
            "an empty-body 4xx gets re-executed through the GET-only /not-found page — a non-GET /api call would surface as 405");
    }

    [Fact]
    public async Task ApiPath_ForbiddenStatus_Writes403()
    {
        var ctx = RedirectCtx("/api/settings/households");

        await ApiAwareAuthEvents.StatusForApiElseRedirect(ctx, StatusCodes.Status403Forbidden);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status403Forbidden);
        ctx.Response.Body.Length.Should().BeGreaterThan(0,
            "an empty-body 4xx gets re-executed through the GET-only /not-found page — a non-GET /api call would surface as 405");
    }

    [Theory]
    [InlineData("/dashboard")]
    [InlineData("/chores")]
    [InlineData("/")]
    public async Task NonApiPath_Redirects_AndDoesNotWriteApiStatus(string path)
    {
        var ctx = RedirectCtx(path);

        await ApiAwareAuthEvents.StatusForApiElseRedirect(ctx, StatusCodes.Status401Unauthorized);

        ctx.Response.StatusCode.Should().Be(StatusCodes.Status302Found);
        ctx.Response.Headers.Location.ToString().Should().Be("/account/login");
    }
}
