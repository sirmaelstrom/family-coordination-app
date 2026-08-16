using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

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
}
