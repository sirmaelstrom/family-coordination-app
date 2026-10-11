using System.Net;
using System.Net.Http.Json;
using System.Security.Claims;
using System.Text.Json;
using System.Text.RegularExpressions;
using FamilyCoordinationApp.Data;
using FluentAssertions;
using FluentAssertions.Execution;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Patterns;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Authorization beyond login, stated once (quest 82fc7f54).
/// <list type="bullet">
/// <item><b>The /api login gate.</b> An /api endpoint that carries no authorization metadata of its own is refused for
/// an anonymous caller. The probe facts run a synthetic, unguarded endpoint through the app's real
/// <see cref="AuthorizationMiddleware"/> and DI, which is the "a new group that forgets
/// <c>RequireAuthorization()</c>" case; the sweep fact sends a real anonymous request to every mapped /api route.</item>
/// <item><b>The site-admin policy.</b> Every site-admin route carries the one <c>SiteAdmin</c> policy, and a
/// non-admin gets a 403 with the policy's message on each.</item>
/// <item><b>The anonymous surface.</b> The routes that must stay public still answer as they did before the gate.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class CentralAuthorizationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private const string SiteAdminPolicy = "SiteAdmin";

    /// <summary>The only /api routes an anonymous caller may reach: the shared-secret digest cron and the
    /// capability-token calendar feed. Each must say so with an explicit <c>AllowAnonymous()</c>.</summary>
    internal static readonly string[] PinnedAnonymousApiRoutes =
    [
        "/api/chores/digest/run",
        "/api/calendar/meal-plan.ics",
    ];

    /// <summary>The site-admin routes: global reads/writes across households.</summary>
    internal static readonly string[] SiteAdminRoutes =
    [
        "/api/settings/household-requests/",
        "/api/settings/household-requests/{id:int}/approve",
        "/api/settings/household-requests/{id:int}/reject",
        "/api/settings/households/",
    ];

    private sealed record ErrorBody(string message);

    private List<RouteEndpoint> ApiEndpoints() =>
        _factory.Services.GetRequiredService<EndpointDataSource>().Endpoints
            .OfType<RouteEndpoint>()
            .Where(e => (e.RoutePattern.RawText ?? "").StartsWith("/api", StringComparison.OrdinalIgnoreCase))
            .ToList();

    private static string RouteOf(RouteEndpoint e) => e.RoutePattern.RawText ?? "";

    /// <summary>A concrete URL for a route template: every parameter becomes <c>1</c>.</summary>
    private static string UrlFor(RouteEndpoint e) => Regex.Replace(RouteOf(e), @"\{[^}]+\}", "1");

    private static IEnumerable<string> MethodsOf(RouteEndpoint e) =>
        e.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods ?? ["GET"];

    // ── The /api login gate ────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Runs one request through the app's real <see cref="AuthorizationMiddleware"/>, resolved from the booted host,
    /// against a synthetic endpoint that carries NO authorization metadata. Returns whether the endpoint ran and the
    /// status the auth pipeline left.
    /// </summary>
    private async Task<(bool EndpointRan, int Status)> ProbeUnguardedEndpointAsync(string path, ClaimsPrincipal user)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var http = new DefaultHttpContext { RequestServices = scope.ServiceProvider, User = user };
        http.Request.Method = HttpMethods.Get;
        http.Request.Path = path;
        http.Response.Body = new MemoryStream();

        var endpointRan = false;
        var endpoint = new RouteEndpointBuilder(
            _ => Task.CompletedTask, RoutePatternFactory.Parse(path), order: 0).Build();
        http.SetEndpoint(endpoint);

        var middleware = new AuthorizationMiddleware(
            _ =>
            {
                endpointRan = true;
                return Task.CompletedTask;
            },
            scope.ServiceProvider.GetRequiredService<IAuthorizationPolicyProvider>(),
            scope.ServiceProvider);
        await middleware.Invoke(http);

        return (endpointRan, http.Response.StatusCode);
    }

    private static ClaimsPrincipal Anonymous() => new(new ClaimsIdentity());

    private static ClaimsPrincipal SignedIn(string email) => new(new ClaimsIdentity(
        [new Claim(ClaimTypes.Email, email), new Claim(ClaimTypes.Name, email)], ChoresWebAppFactory.TestAuthScheme));

    [Fact]
    public async Task An_api_endpoint_without_its_own_RequireAuthorization_refuses_an_anonymous_caller()
    {
        var (ran, status) = await ProbeUnguardedEndpointAsync("/api/probe/unguarded", Anonymous());

        ran.Should().BeFalse("an /api endpoint that forgets RequireAuthorization() must not be anonymous");
        status.Should().Be(StatusCodes.Status401Unauthorized);
    }

    [Fact]
    public async Task An_api_endpoint_without_its_own_RequireAuthorization_admits_a_whitelisted_member()
    {
        var (ran, _) = await ProbeUnguardedEndpointAsync("/api/probe/unguarded", SignedIn(ChoresWebAppFactory.UserAEmail));

        ran.Should().BeTrue("the gate is the default policy (signed in + whitelisted), not a blanket refusal");
    }

    [Fact]
    public async Task An_api_endpoint_without_its_own_RequireAuthorization_refuses_a_non_whitelisted_caller()
    {
        var (ran, status) = await ProbeUnguardedEndpointAsync("/api/probe/unguarded", SignedIn("stranger@nowhere.test"));

        ran.Should().BeFalse("the gate carries the whitelist requirement, like the default policy");
        status.Should().Be(StatusCodes.Status403Forbidden);
    }

    [Fact]
    public async Task A_non_api_endpoint_without_authorization_metadata_stays_anonymous()
    {
        var (ran, _) = await ProbeUnguardedEndpointAsync("/probe/unguarded", Anonymous());

        ran.Should().BeTrue("the gate is scoped to /api: Razor Pages, static files and the SPA fallback are untouched");
    }

    [Fact]
    public async Task Every_api_route_refuses_an_anonymous_caller_except_the_pinned_public_ones()
    {
        var endpoints = ApiEndpoints();
        endpoints.Count.Should().BeGreaterThan(80, "guard the guard: the /api population was 114 routes at quest 82fc7f54");

        using var client = _factory.CreateAnonymousClient();
        var admitted = new List<string>();
        foreach (var endpoint in endpoints.Where(e => !PinnedAnonymousApiRoutes.Contains(RouteOf(e))))
        {
            foreach (var method in MethodsOf(endpoint))
            {
                using var request = new HttpRequestMessage(new HttpMethod(method), UrlFor(endpoint));
                if (method is "POST" or "PUT" or "PATCH")
                {
                    request.Content = JsonContent.Create(new { }, options: Json);
                }

                using var resp = await client.SendAsync(request);
                if (resp.StatusCode != HttpStatusCode.Unauthorized)
                {
                    admitted.Add($"{method} {RouteOf(endpoint)} -> {(int)resp.StatusCode}");
                }
            }
        }

        admitted.Should().BeEmpty("every /api route but the pinned public ones must answer an anonymous caller 401");
    }

    [Fact]
    public void The_pinned_public_api_routes_are_exactly_the_ones_marked_AllowAnonymous()
    {
        var allowAnonymous = ApiEndpoints()
            .Where(e => e.Metadata.GetMetadata<IAllowAnonymous>() is not null)
            .Select(RouteOf);

        allowAnonymous.Should().BeEquivalentTo(PinnedAnonymousApiRoutes,
            "an anonymous /api route says so explicitly; it is never anonymous by omission");
    }

    // ── The site-admin policy ──────────────────────────────────────────────────────────────────

    [Fact]
    public void The_site_admin_routes_are_exactly_the_routes_carrying_the_SiteAdmin_policy()
    {
        var withPolicy = ApiEndpoints()
            .Where(e => e.Metadata.GetOrderedMetadata<IAuthorizeData>().Any(a => a.Policy == SiteAdminPolicy))
            .Select(RouteOf);

        withPolicy.Should().BeEquivalentTo(SiteAdminRoutes);
    }

    public static TheoryData<string, string, string?> SiteAdminRequests() => new()
    {
        { "GET", "/api/settings/household-requests/", null },
        { "POST", "/api/settings/household-requests/1/approve", null },
        { "POST", "/api/settings/household-requests/1/reject", """{"reason":"no"}""" },
        { "POST", "/api/settings/households/", """{"householdName":"Rogue","ownerEmail":"rogue@example.com"}""" },
    };

    [Theory]
    [MemberData(nameof(SiteAdminRequests))]
    public async Task A_non_admin_is_refused_on_every_site_admin_route_with_403_and_a_message(
        string method, string url, string? body)
    {
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);
        using var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        using var resp = await client.SendAsync(request);

        using (new AssertionScope())
        {
            resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
            var error = await resp.Content.ReadFromJsonAsync<ErrorBody>(Json);
            error!.message.Should().Be("Site admin access required.");
        }

        await using var db = await _factory.Services
            .GetRequiredService<IDbContextFactory<ApplicationDbContext>>().CreateDbContextAsync();
        (await db.Households.CountAsync()).Should().Be(2, "a refused admin write creates nothing");
    }

    [Fact]
    public async Task The_site_admin_passes_the_policy()
    {
        using var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        using var resp = await client.GetAsync("/api/settings/household-requests/");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── The anonymous surface still answers as before ──────────────────────────────────────────

    /// <summary>
    /// Anonymous routes off /api, with the status each returned on the pre-change tree (base d79f663, measured by this
    /// fact before the gate landed).
    /// </summary>
    public static TheoryData<string, string, HttpStatusCode> AnonymousSurface() => new()
    {
        { "GET", "/health", HttpStatusCode.OK },
        { "GET", "/account/login", HttpStatusCode.OK },
        { "GET", "/account/access-denied", HttpStatusCode.OK },
        { "GET", "/privacy", HttpStatusCode.OK },
        { "GET", "/terms", HttpStatusCode.OK },
        { "GET", "/household/request", HttpStatusCode.OK },
        { "GET", "/account/logout", HttpStatusCode.Redirect },
        { "GET", "/favicon-16x16.png", HttpStatusCode.OK },
        { "GET", "/uploads/1/photo.jpg", HttpStatusCode.Unauthorized },
        { "GET", "/no-such-page", HttpStatusCode.NotFound },
        { "GET", "/api/calendar/meal-plan.ics?token=not-a-real-token", HttpStatusCode.NotFound },
        { "POST", "/api/chores/digest/run", HttpStatusCode.Unauthorized },
    };

    [Theory]
    [MemberData(nameof(AnonymousSurface))]
    public async Task An_anonymous_route_answers_as_it_did_before_the_gate(string method, string url, HttpStatusCode expected)
    {
        using var client = _factory.CreateAnonymousClient();

        using var resp = await client.SendAsync(new HttpRequestMessage(new HttpMethod(method), url));

        resp.StatusCode.Should().Be(expected);
    }

    [Theory]
    [InlineData("/")]
    [InlineData("/dashboard")]
    [InlineData("/chores/today")]
    public async Task The_SPA_fallback_is_not_challenged_for_an_anonymous_caller(string url)
    {
        // The SPA shell boots anonymously and then calls /api/me. Whether wwwroot/index.html exists depends on whether
        // the SPA was built, so the fact is the absence of a challenge, not a specific status.
        using var client = _factory.CreateAnonymousClient();

        using var resp = await client.GetAsync(url);

        resp.StatusCode.Should().NotBe(HttpStatusCode.Unauthorized);
        resp.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
        resp.Headers.Location.Should().BeNull("an anonymous page load is never redirected to sign-in by the gate");
    }
}
