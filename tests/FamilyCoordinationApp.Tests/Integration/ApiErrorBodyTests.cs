using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The non-empty-/api-4xx invariant, enforced by the pipeline rather than by every call site.
/// <para><c>UseStatusCodePagesWithReExecute("/not-found")</c> re-runs a bodiless error through a GET-only Razor
/// Page, which rewrites the status a non-GET caller observes — measured: a POST to a GET-only /api route arrived
/// as an empty <b>400</b> when routing had actually produced a <b>405</b>. <c>/api</c> is now branched to
/// <c>UseStatusCodePages</c>, which backfills a JSON body and leaves the status alone.</para>
/// <para>These tests assert the GUARANTEE, not the call sites: they must keep passing as handlers are written,
/// including handlers that return a bodiless result. The controls are as load-bearing as the cases — a backfill
/// that also touched successful or already-bodied responses would be a worse bug than the one being fixed.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ApiErrorBodyTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);

    public Task InitializeAsync() => _factory.EnsureSeededAsync();

    public Task DisposeAsync() => _factory.DisposeAsync().AsTask();

    private HttpClient Client => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    /// <param name="body">JSON to send, where the route requires one (chore delete carries its xmin version).</param>
    public static TheoryData<string, string, string?, HttpStatusCode> BodilessErrorRoutes() => new()
    {
        // The bodiless `Results.NotFound()` sites named by the friction review, including two of the four
        // DELETE routes where an empty body previously changed the status the client saw.
        { "DELETE", "/api/rooms/999999", null, HttpStatusCode.NotFound },
        { "DELETE", "/api/chores/999999", """{"version":1}""", HttpStatusCode.NotFound },
        { "GET", "/api/shopping-lists/999999", null, HttpStatusCode.NotFound },
        // Raised by ROUTING, before any handler runs — the class an endpoint filter could never have covered.
        // The 405 case depends on /api/dashboard mapping GET only (DashboardEndpoints). If a POST is ever added
        // there this row starts failing for an unrelated reason — repoint it at another GET-only route.
        { "POST", "/api/dashboard/", null, HttpStatusCode.MethodNotAllowed },
        { "GET", "/api/no-such-route", null, HttpStatusCode.NotFound },
    };

    [Theory]
    [MemberData(nameof(BodilessErrorRoutes))]
    public async Task ApiError_CarriesJsonBody_AndKeepsItsStatus(string method, string url, string? body, HttpStatusCode expected)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }

        var resp = await Client.SendAsync(request);

        resp.StatusCode.Should().Be(expected, "the status must survive — re-execution used to rewrite it");

        var received = await resp.Content.ReadAsStringAsync();
        received.Should().NotBeNullOrWhiteSpace();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        JsonDocument.Parse(received).RootElement.TryGetProperty("message", out var message).Should().BeTrue();
        message.GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>
    /// The auth family — the largest population of bodiless results (99 `Results.Unauthorized()` call sites) and
    /// the one reached through the pipeline rather than a handler return. Asserts the GUARANTEE (a 4xx carrying a
    /// JSON message) rather than a specific code, because which of 401/403 a caller gets is the authorization
    /// stack's business and is asserted by ChoreEndpointAuthTests.
    /// </summary>
    [Theory]
    [InlineData("anonymous")]
    [InlineData("authenticated-but-not-whitelisted")]
    public async Task ApiAuthFailure_CarriesJsonBody(string caller)
    {
        var client = caller == "anonymous"
            ? _factory.CreateAnonymousClient()
            : _factory.CreateClientAs("nobody@not-a-member.test");

        var resp = await client.GetAsync("/api/chores/board");

        ((int)resp.StatusCode).Should().BeInRange(400, 499, "an auth failure on /api is a client rejection");
        var received = await resp.Content.ReadAsStringAsync();
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json", "never HTML, never empty");
        JsonDocument.Parse(received).RootElement.TryGetProperty("message", out var message).Should().BeTrue();
        message.GetString().Should().NotBeNullOrWhiteSpace();
    }

    /// <summary>A handler that wrote its own message keeps it — the backfill is a floor, not an overwrite.</summary>
    [Fact]
    public async Task ApiError_WithItsOwnBody_IsNotOverwritten()
    {
        var resp = await Client.PostAsJsonAsync(
            "/api/settings/feedback/", new { type = "bug", message = "   " }, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("Feedback message is required.");
    }

    [Fact]
    public async Task ApiSuccess_IsNotTouched()
    {
        var resp = await Client.GetAsync("/api/me");

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        (await resp.Content.ReadAsStringAsync()).Should().Contain("householdId");
    }

    /// <summary>
    /// The other half of the branch: a NON-/api miss must still re-execute through the Razor page, or this change
    /// would have silently turned the app's 404 page into a JSON blob.
    /// </summary>
    [Fact]
    public async Task NonApiNotFound_StillRendersTheHtmlPage()
    {
        var resp = await Client.GetAsync("/definitely-not-a-route");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("text/html");
    }

    /// <summary>
    /// Failed <c>[FromBody]</c> binds on /api answer JSON in the NON-Development pipeline. MEASURED at
    /// pickup (quest ea816df2): these passed before the exception branch existed, because outside
    /// Development a bind failure does NOT throw (<c>ThrowOnBadRequest</c> is dev-only) — it produces a
    /// bodiless 400 that the status-code backfill covers. Kept as regression guards for that composed
    /// behavior; the Development half, where the same request THROWS, is the test below.
    /// </summary>
    public static TheoryData<string, string, string?, string> BindFailureRequests() => new()
    {
        { "DELETE", "/api/chores/999999", null, "required [FromBody] VersionRequest omitted entirely" },
        { "DELETE", "/api/chores/999999", "{ not json", "malformed JSON in the request body" },
    };

    [Theory]
    [MemberData(nameof(BindFailureRequests))]
    public async Task ApiBindFailure_AnswersJson_WithoutLeakingDetail(string method, string url, string? body, string because)
    {
        var resp = await SendBindFailure(Client, method, url, body);

        await AssertCleanJsonError(resp, because);
    }

    /// <summary>
    /// The EXCEPTION path proper — the half <c>UseStatusCodePages</c> structurally cannot reach. In
    /// Development a failed bind THROWS <c>BadHttpRequestException</c> before the handler runs, and used to
    /// answer a text/plain stack trace (measured on a live dev stack, 2026-08-15). The /api exception
    /// branch must convert it to the same JSON <c>{message}</c> the rest of the contract promises.
    /// </summary>
    [Theory]
    [MemberData(nameof(BindFailureRequests))]
    public async Task ApiExceptionPath_InDevelopment_AnswersJson_WithoutLeakingDetail(string method, string url, string? body, string because)
    {
        using var devFactory = _factory.WithWebHostBuilder(b => b.UseEnvironment("Development"));
        using var client = devFactory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, ChoresWebAppFactory.UserAEmail);

        var resp = await SendBindFailure(client, method, url, body);

        await AssertCleanJsonError(resp, because);
    }

    /// <summary>
    /// The page half must keep its behavior: a non-/api error response stays page-shaped, never the /api
    /// JSON writer's output.
    /// </summary>
    [Fact]
    public async Task NonApiErrorHandling_IsUntouched()
    {
        // /household/request has an antiforgery-validated OnPost; posting without a token is the cheapest
        // reliably-erroring non-/api POST the real pipeline offers.
        var resp = await Client.PostAsync("/household/request", new FormUrlEncodedContent([]));

        ((int)resp.StatusCode).Should().BeGreaterThanOrEqualTo(400);
        resp.Content.Headers.ContentType?.MediaType.Should().NotBe("application/json",
            "non-/api error handling must stay page-shaped");
    }

    private static async Task<HttpResponseMessage> SendBindFailure(HttpClient client, string method, string url, string? body)
    {
        var request = new HttpRequestMessage(new HttpMethod(method), url);
        if (body is not null)
        {
            request.Content = new StringContent(body, System.Text.Encoding.UTF8, "application/json");
        }
        return await client.SendAsync(request);
    }

    private static async Task AssertCleanJsonError(HttpResponseMessage resp, string because)
    {
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, because);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json", because);

        var received = await resp.Content.ReadAsStringAsync();
        JsonDocument.Parse(received).RootElement.TryGetProperty("message", out var message).Should().BeTrue();
        message.GetString().Should().NotBeNullOrWhiteSpace();

        // The defect classes this guards: a stack trace (Development) or an HTML error page (elsewhere).
        received.Should().NotContainAny(["Exception", "<html", "   at "],
            "an /api error body must never leak exception detail or arrive as a page");
    }
}
