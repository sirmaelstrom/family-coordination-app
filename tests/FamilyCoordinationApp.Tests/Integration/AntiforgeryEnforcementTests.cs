using System.Net;
using System.Text.RegularExpressions;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Locks in the WP-11 antiforgery posture end-to-end against the real <see cref="Program"/> pipeline
/// (previously only verified manually):
/// <list type="bullet">
/// <item>A token-less POST to the onboarding Razor Pages (<c>/household/request</c>, <c>/setup</c>) is
/// rejected with 400 by the framework's automatic Razor Pages antiforgery validation — it must never reach
/// the <c>OnPostAsync</c> handler (which would respond with a 302 redirect).</item>
/// <item>Login-CSRF hardening: a token-less POST to <c>/account/login-google</c> is rejected with 400
/// (a cross-site page must not be able to silently initiate the OAuth challenge and log the victim's
/// browser into an attacker-chosen account), while a same-site form POST carrying the antiforgery token
/// issued by <c>/account/login</c> still initiates the Google challenge (302 to accounts.google.com) —
/// proving the hardening did not break the real sign-in flow.</item>
/// </list>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class AntiforgeryEnforcementTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    [Fact]
    public async Task HouseholdRequest_TokenlessPost_IsRejectedWith400()
    {
        // Authenticated on purpose: if the antiforgery gate were missing, this request would reach
        // OnPostAsync and come back as a 302 (redirect to /household/pending) — so a 400 here proves the
        // request was rejected BEFORE the handler, by antiforgery validation specifically.
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.PostAsync("/household/request", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["HouseholdName"] = "CSRF Household" }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a POST without an antiforgery token must be rejected by Razor Pages auto-validation");
    }

    [Fact]
    public async Task FirstRunSetup_TokenlessPost_IsRejectedWith400()
    {
        // Same shape as above: a reached handler would 302 (setup is already complete in the seeded
        // fixture → redirect to /dashboard), so 400 proves the antiforgery gate fired first.
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.PostAsync("/setup", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["HouseholdName"] = "CSRF Household" }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "a POST without an antiforgery token must be rejected by Razor Pages auto-validation");
    }

    [Fact]
    public async Task LoginGoogle_TokenlessPost_IsRejectedWith400_WithNonEmptyBody()
    {
        // Login-CSRF: a bare cross-site form POST (no antiforgery token) must not initiate the OAuth
        // challenge. If the gate were missing this would be a 302 to accounts.google.com.
        var client = _factory.CreateAnonymousClient();

        var resp = await client.PostAsync("/account/login-google", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["returnUrl"] = "/" }));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest,
            "initiating the OAuth challenge without an antiforgery token must be rejected");

        // Non-empty /error body invariant: an empty 4xx re-executes through the GET-only /not-found page
        // and a non-GET call surfaces as a 405 (CORRECTIONS fca-empty-404-surfaces-as-405-on-delete).
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("4xx responses must carry a body");
    }

    [Fact]
    public async Task LoginGoogle_PostWithValidToken_StillChallengesGoogle()
    {
        // The real sign-in path: GET /account/login issues the antiforgery cookie + form token; posting
        // both back must still initiate the Google OAuth challenge (302 to accounts.google.com). This
        // pins the "don't break the actual OAuth flow" half of the hardening. The WebApplicationFactory
        // client handles cookies, so the antiforgery cookie set by the GET flows into the POST.
        var client = _factory.CreateAnonymousClient();

        var loginPage = await client.GetAsync("/account/login");
        loginPage.StatusCode.Should().Be(HttpStatusCode.OK);

        var html = await loginPage.Content.ReadAsStringAsync();
        var match = Regex.Match(html,
            "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        match.Success.Should().BeTrue("the login page's sign-in form must embed an antiforgery token");
        var token = match.Groups[1].Value;

        var resp = await client.PostAsync("/account/login-google", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["__RequestVerificationToken"] = token,
                ["returnUrl"] = "/dashboard"
            }));

        resp.StatusCode.Should().Be(HttpStatusCode.Found,
            "a token-carrying form POST must still initiate the OAuth challenge");
        resp.Headers.Location.Should().NotBeNull();
        resp.Headers.Location!.Host.Should().Be("accounts.google.com",
            "the challenge must redirect to Google's authorization endpoint");
    }
}
