using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Calendar;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class CalendarTokenEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private sealed record TokenResponse(string token, string url);
    private sealed record TokenStatus(bool active, DateTime? createdAt, bool sharedLinkActive, DateTime? sharedLinkCreatedAt);
    private sealed record RecipeSummary(int recipeId);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientA2 => _factory.CreateClientAs(ChoresWebAppFactory.UserA2Email);
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

        var statusResponse = await ClientA.GetAsync("/api/meal-plan/calendar-token");
        var statusJson = await statusResponse.Content.ReadAsStringAsync();
        statusJson.Should().NotContain(created.token);
        var status = JsonSerializer.Deserialize<TokenStatus>(statusJson, Json);
        status.Should().NotBeNull();
        status!.active.Should().BeTrue();
        status.createdAt.Should().NotBeNull();

        await using var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync();
        var stored = await context.HouseholdCalendarTokens
            .SingleAsync(calendarToken => calendarToken.HouseholdId == ChoresWebAppFactory.HouseholdAId && calendarToken.RevokedAt == null);
        stored.TokenHash.Should().HaveLength(64);
        stored.TokenHash.Should().NotBe(created.token);
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
    public async Task TwoRotations_LeaveExactlyOneActiveToken()
    {
        await CreateTokenAsync(ClientA);
        await CreateTokenAsync(ClientA);

        await using var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync();
        (await context.HouseholdCalendarTokens.CountAsync(calendarToken =>
            calendarToken.HouseholdId == ChoresWebAppFactory.HouseholdAId && calendarToken.RevokedAt == null))
            .Should().Be(1);
    }

    [Fact]
    public async Task FourConcurrentRotations_CompleteWithOneActiveTokenForThatMember_AndLeaveAnotherMembersLink()
    {
        const int rotations = 4;
        var other = await CreateTokenAsync(ClientA2);
        var service = new HouseholdCalendarTokenService(
            new PostgresDbContextFactory(_factory.ConnectionString, TestTenancy.Caller(ChoresWebAppFactory.HouseholdAId)),
            Services.TestClocks.System);

        var created = await Task.WhenAll(Enumerable.Range(0, rotations)
            .Select(_ => service.CreateOrRotateAsync(ChoresWebAppFactory.HouseholdAId, ChoresWebAppFactory.UserAId, CalendarFeed.MealPlan)));

        created.Should().HaveCount(rotations);
        await using var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync();
        var active = await context.HouseholdCalendarTokens.SingleAsync(calendarToken =>
            calendarToken.UserId == ChoresWebAppFactory.UserAId && calendarToken.RevokedAt == null);
        active.TokenHash.Should().BeOneOf(created.Select(token => Hash(token.Token)).ToArray());
        (await context.HouseholdCalendarTokens.SingleAsync(calendarToken =>
            calendarToken.UserId == ChoresWebAppFactory.UserA2Id && calendarToken.RevokedAt == null))
            .TokenHash.Should().Be(Hash(other.token), "another member's link is not part of this member's rotation");
    }

    // ── Per-member links (quest 8dba87f4): one member's rotate or revoke never breaks another's subscription ──

    [Fact]
    public async Task OneMembersRotation_LeavesAnotherMembersSubscriptionReading_AndKillsOnlyTheirOwnOldUrl()
    {
        await AddMealAsync(ClientA, "Shared household meal", "2026-06-02");
        var aFirst = await CreateTokenAsync(ClientA);
        var a2 = await CreateTokenAsync(ClientA2);
        var anonymous = _factory.CreateAnonymousClient();

        var aSecond = await CreateTokenAsync(ClientA);

        var a2Feed = await anonymous.GetAsync(a2.url);
        a2Feed.StatusCode.Should().Be(HttpStatusCode.OK, "A's rotation must not touch A2's link");
        (await a2Feed.Content.ReadAsStringAsync()).Should().Contain("Shared household meal");
        var aOld = await anonymous.GetAsync(aFirst.url);
        aOld.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await aOld.Content.ReadAsStringAsync()).Should().BeEmpty();
        (await anonymous.GetAsync(aSecond.url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task OneMembersRevoke_LeavesAnotherMembersSubscriptionReading()
    {
        var a = await CreateTokenAsync(ClientA);
        var a2 = await CreateTokenAsync(ClientA2);
        var anonymous = _factory.CreateAnonymousClient();

        (await ClientA.DeleteAsync("/api/meal-plan/calendar-token")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await anonymous.GetAsync(a.url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(a2.url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task EveryMembersFeed_IsTheSameHouseholdPlan()
    {
        await AddMealAsync(ClientA2, "Meal A2 planned", "2026-06-03");
        var a = await CreateTokenAsync(ClientA);
        var a2 = await CreateTokenAsync(ClientA2);
        var anonymous = _factory.CreateAnonymousClient();

        var aEvents = Events(await anonymous.GetStringAsync(a.url));
        var a2Events = Events(await anonymous.GetStringAsync(a2.url));

        aEvents.Should().ContainSingle(line => line.Contains("Meal A2 planned"));
        aEvents.Should().Equal(a2Events, "one household plan, so the same events and UIDs on every member's link");
    }

    [Fact]
    public async Task StatusIsPerMember()
    {
        await CreateTokenAsync(ClientA);

        var a = await GetStatusAsync(ClientA);
        var a2 = await GetStatusAsync(ClientA2);

        a.active.Should().BeTrue();
        a2.active.Should().BeFalse("A2 has no link of their own yet");
        a2.sharedLinkActive.Should().BeFalse();
    }

    [Fact]
    public async Task AnotherHouseholdsMemberLink_NeverReadsThisHouseholdsPlan()
    {
        await AddMealAsync(ClientA, "A only meal", "2026-06-02");
        await AddMealAsync(ClientB, "B only meal", "2026-06-02");
        var b = await CreateTokenAsync(ClientB);

        var calendar = await _factory.CreateAnonymousClient().GetStringAsync(b.url);

        calendar.Should().Contain("B only meal");
        calendar.Should().NotContain("A only meal");
    }

    [Fact]
    public async Task DisabledMembersLink_IsAnEmpty404_AndAnotherMembersStillReads()
    {
        var a = await CreateTokenAsync(ClientA);
        var a2 = await CreateTokenAsync(ClientA2);
        var anonymous = _factory.CreateAnonymousClient();
        await SetWhitelistedAsync(ChoresWebAppFactory.UserA2Id, false);

        var disabled = await anonymous.GetAsync(a2.url);

        disabled.StatusCode.Should().Be(HttpStatusCode.NotFound, "a disabled member is no longer a reader of the plan");
        (await disabled.Content.ReadAsStringAsync()).Should().BeEmpty("the same no-oracle 404 as an unknown token");
        (await anonymous.GetAsync(a.url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task DeletedMembersLink_IsAnEmpty404()
    {
        const string email = "carol@household-a.test";
        (await ClientA.PostAsJsonAsync("/api/settings/members", new { email }, Json)).StatusCode.Should().Be(HttpStatusCode.OK);
        var carol = await CreateTokenAsync(_factory.CreateClientAs(email));
        int carolId;
        await using (var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync())
        {
            carolId = await context.Users.Where(user => user.Email == email).Select(user => user.Id).SingleAsync();
        }

        (await ClientA.DeleteAsync($"/api/settings/members/{carolId}")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        var response = await _factory.CreateAnonymousClient().GetAsync(carol.url);
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        await using var check = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync();
        (await check.HouseholdCalendarTokens.CountAsync(token => token.UserId == carolId))
            .Should().Be(0, "the member's links go with the member (FK cascade)");
    }

    [Fact]
    public async Task CreatingALinkForAUserOfAnotherHousehold_IsRefused()
    {
        var service = new HouseholdCalendarTokenService(
            new PostgresDbContextFactory(_factory.ConnectionString, TestTenancy.Caller(ChoresWebAppFactory.HouseholdAId)),
            Services.TestClocks.System);

        var act = () => service.CreateOrRotateAsync(ChoresWebAppFactory.HouseholdAId, ChoresWebAppFactory.UserBId, CalendarFeed.MealPlan);

        await act.Should().ThrowAsync<FamilyCoordinationApp.Services.HouseholdMembershipException>();
        await using var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync();
        (await context.HouseholdCalendarTokens.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task MemberLinkFromAnotherHousehold_IsAnEmpty404()
    {
        // A row naming household A but a user of household B (not writable through the API: the user is the caller).
        // Seeded through the tenant's own context, so the write step stamps household A on it.
        var token = NewToken();
        await using (var context = await new PostgresDbContextFactory(
                         _factory.ConnectionString, TestTenancy.Caller(ChoresWebAppFactory.HouseholdAId)).CreateDbContextAsync())
        {
            context.HouseholdCalendarTokens.Add(new HouseholdCalendarToken
            {
                UserId = ChoresWebAppFactory.UserBId,
                Feed = CalendarFeed.MealPlan,
                TokenHash = Hash(token),
                CreatedAt = DateTime.UtcNow,
            });
            await context.SaveChangesAsync();
        }

        var response = await _factory.CreateAnonymousClient().GetAsync("/api/calendar/meal-plan.ics?token=" + token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound, "the token's member must belong to the token's household");
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task LegacySharedLink_KeepsReading_IsReported_AndIsRevokedOnlyByItsOwnRoute()
    {
        var shared = await SeedLegacySharedTokenAsync();
        await CreateTokenAsync(ClientA);
        var anonymous = _factory.CreateAnonymousClient();
        var sharedUrl = "/api/calendar/meal-plan.ics?token=" + shared;

        (await anonymous.GetAsync(sharedUrl)).StatusCode.Should().Be(HttpStatusCode.OK, "deploy day must not break the shared link");
        (await GetStatusAsync(ClientA2)).sharedLinkActive.Should().BeTrue();

        await CreateTokenAsync(ClientA); // A's rotation is A's own: it leaves the shared link alone
        (await anonymous.GetAsync(sharedUrl)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClientA.DeleteAsync("/api/meal-plan/calendar-token")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anonymous.GetAsync(sharedUrl)).StatusCode.Should().Be(HttpStatusCode.OK, "revoking your own link is not revoking the shared one");

        (await ClientA2.DeleteAsync("/api/meal-plan/calendar-token/shared")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await anonymous.GetAsync(sharedUrl)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        var status = await GetStatusAsync(ClientA);
        status.sharedLinkActive.Should().BeFalse();
        status.sharedLinkCreatedAt.Should().BeNull();
    }

    [Fact]
    public async Task RevokingTheSharedLink_LeavesMemberLinks()
    {
        await SeedLegacySharedTokenAsync();
        var a = await CreateTokenAsync(ClientA);

        (await ClientA2.DeleteAsync("/api/meal-plan/calendar-token/shared")).StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await _factory.CreateAnonymousClient().GetAsync(a.url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task<string> SeedLegacySharedTokenAsync()
    {
        var token = NewToken();
        await using var context = await new PostgresDbContextFactory(
            _factory.ConnectionString, TestTenancy.Caller(ChoresWebAppFactory.HouseholdAId)).CreateDbContextAsync();
        context.HouseholdCalendarTokens.Add(new HouseholdCalendarToken
        {
            UserId = null,
            Feed = CalendarFeed.MealPlan,
            TokenHash = Hash(token),
            CreatedAt = DateTime.UtcNow,
        });
        await context.SaveChangesAsync();
        return token;
    }

    private async Task SetWhitelistedAsync(int userId, bool whitelisted)
    {
        await using var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync();
        await context.Users
            .Where(user => user.Id == userId)
            .ExecuteUpdateAsync(update => update.SetProperty(user => user.IsWhitelisted, whitelisted));
    }

    private static async Task<TokenStatus> GetStatusAsync(HttpClient client)
    {
        var status = await client.GetFromJsonAsync<TokenStatus>("/api/meal-plan/calendar-token", Json);
        status.Should().NotBeNull();
        return status!;
    }

    /// <summary>The VEVENT lines of a calendar, minus the per-response DTSTAMP.</summary>
    private static List<string> Events(string calendar) =>
        calendar.Split("\r\n")
            .SkipWhile(line => line != "BEGIN:VEVENT")
            .Where(line => !line.StartsWith("DTSTAMP", StringComparison.Ordinal))
            .ToList();

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

    /// <summary>A well-formed capability secret (the service's own shape: 32 random bytes, base64url, 43 chars).</summary>
    private static string NewToken() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(32)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}
