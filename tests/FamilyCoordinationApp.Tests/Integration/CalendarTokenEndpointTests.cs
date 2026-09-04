using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class CalendarTokenEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record TokenResponse(string token, string url);
    private sealed record TokenStatus(bool active, DateTime? createdAt);
    private sealed record RecipeSummary(int recipeId);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    [Fact]
    public async Task AnonymousFeed_ReturnsOnlyItsHouseholdsEntries_AsCalendar()
    {
        await AddMealAsync(ClientA, "A calendar meal", "2026-06-02");
        await AddMealAsync(ClientA, "A fourth-week meal", "2026-06-23");
        await AddMealAsync(ClientB, "B private calendar meal", "2026-06-02");
        var created = await CreateTokenAsync(ClientA);

        var response = await _factory.CreateAnonymousClient().GetAsync(created.url);
        var calendar = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/calendar");
        response.Content.Headers.ContentType.CharSet.Should().Be("utf-8");
        response.Headers.CacheControl!.Private.Should().BeTrue();
        response.Headers.CacheControl.NoStore.Should().BeTrue();
        calendar.Should().Contain("A calendar meal");
        calendar.Should().NotContain("B private calendar meal");
        calendar.Should().Contain("A fourth-week meal");
        calendar.Split("BEGIN:VEVENT", StringSplitOptions.None).Length.Should().Be(3);

        var status = await ClientA.GetFromJsonAsync<TokenStatus>("/api/meal-plan/calendar-token", Json);
        status.Should().NotBeNull();
        status!.active.Should().BeTrue();
        status.createdAt.Should().NotBeNull();
    }

    [Fact]
    public async Task RotationAndRevocation_InvalidateOldCapabilities()
    {
        var first = await CreateTokenAsync(ClientA);
        var second = await CreateTokenAsync(ClientA);
        var anonymous = _factory.CreateAnonymousClient();

        (await anonymous.GetAsync(first.url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(second.url)).StatusCode.Should().Be(HttpStatusCode.OK);

        var revoke = await ClientA.DeleteAsync("/api/meal-plan/calendar-token");
        revoke.StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anonymous.GetAsync(second.url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task MalformedUnknownAndRevokedCapabilities_AreIdenticalEmpty404s()
    {
        var created = await CreateTokenAsync(ClientA);
        var anonymous = _factory.CreateAnonymousClient();
        await ClientA.DeleteAsync("/api/meal-plan/calendar-token");

        var malformed = await anonymous.GetAsync("/api/calendar/meal-plan.ics?token=not-a-token");
        var unknown = await anonymous.GetAsync("/api/calendar/meal-plan.ics?token=" + new string('A', 43));
        var revoked = await anonymous.GetAsync(created.url);

        var responses = new[] { malformed, unknown, revoked };
        responses.Should().OnlyContain(response => response.StatusCode == HttpStatusCode.NotFound);
        var bodies = await Task.WhenAll(responses.Select(response => response.Content.ReadAsStringAsync()));
        bodies.Should().OnlyContain(body => body == string.Empty);
    }

    private static async Task<TokenResponse> CreateTokenAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/meal-plan/calendar-token", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json);
        token.Should().NotBeNull();
        token!.token.Should().HaveLength(43);
        return token;
    }

    private static async Task AddMealAsync(HttpClient client, string name, string date)
    {
        var recipeResponse = await client.PostAsJsonAsync("/api/meal-plan/recipes", new { name, recipeType = "main" }, Json);
        recipeResponse.StatusCode.Should().Be(HttpStatusCode.Created);
        var recipe = await recipeResponse.Content.ReadFromJsonAsync<RecipeSummary>(Json);
        recipe.Should().NotBeNull();

        var mealResponse = await client.PostAsJsonAsync("/api/meal-plan/entries", new
        {
            date,
            mealType = "dinner",
            recipeId = recipe!.recipeId,
            customMealName = (string?)null,
            notes = "calendar note"
        }, Json);
        mealResponse.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
