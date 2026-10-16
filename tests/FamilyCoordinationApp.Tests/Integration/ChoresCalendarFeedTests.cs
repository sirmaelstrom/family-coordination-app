using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Quest 3bdbcab7: a member's chores feed. It carries the member's assigned and claimed active chores, one all-day
/// event each on the chore's next-due local date (today, marked Overdue, when that date has passed), behind the
/// per-member capability fca#154 introduced, with <c>Feed = Chores</c>. The clock is the factory's fixed instant:
/// local (America/Chicago) Sunday 2026-06-07.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoresCalendarFeedTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly DateTime Now = ChoresWebAppFactory.FixedNowUtc;

    private sealed record TokenResponse(string token, string url);
    private sealed record TokenStatus(bool active, DateTime? createdAt);

    // Household A chores seeded per test (ids above the factory's own).
    private const int FlexibleMine = 101;   // A assigned; Flexible 7d, done local 2026-06-05 → due 2026-06-12
    private const int OneOffClaimed = 102;  // A claimed; OneOff anchored 2026-06-10
    private const int OtherMembers = 103;   // A2 assigned
    private const int DoneMine = 104;       // A assigned, Status Done
    private const int FixedOverdue = 105;   // A assigned; Fixed Mondays, last done 2026-05-25 → slot 2026-06-01 missed

    public async Task InitializeAsync()
    {
        await _factory.EnsureSeededAsync();
        await using var context = await new PostgresDbContextFactory(
            _factory.ConnectionString, TestTenancy.Caller(ChoresWebAppFactory.HouseholdAId)).CreateDbContextAsync();
        context.Chores.AddRange(
            NewChore(FlexibleMine, "Water plants", RecurrenceMode.Flexible, ChoresWebAppFactory.UserAId, AssignmentKind.Assigned,
                intervalDays: 7, lastCompletedAt: new DateTime(2026, 6, 5, 17, 0, 0, DateTimeKind.Utc)),
            NewChore(OneOffClaimed, "Fix the gate", RecurrenceMode.OneOff, ChoresWebAppFactory.UserAId, AssignmentKind.Claimed,
                anchorDate: new DateOnly(2026, 6, 10)),
            NewChore(OtherMembers, "Amy's laundry", RecurrenceMode.Flexible, ChoresWebAppFactory.UserA2Id, AssignmentKind.Assigned,
                intervalDays: 3),
            NewChore(DoneMine, "Finished one-off", RecurrenceMode.OneOff, ChoresWebAppFactory.UserAId, AssignmentKind.Assigned,
                anchorDate: new DateOnly(2026, 6, 9), status: ChoreStatus.Done),
            NewChore(FixedOverdue, "Take out recycling", RecurrenceMode.Fixed, ChoresWebAppFactory.UserAId, AssignmentKind.Assigned,
                daysOfWeek: ChoreDaysOfWeek.Monday, lastCompletedAt: new DateTime(2026, 5, 25, 17, 0, 0, DateTimeKind.Utc)));
        await context.SaveChangesAsync();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientA2 => _factory.CreateClientAs(ChoresWebAppFactory.UserA2Email);

    [Fact]
    public async Task Feed_CarriesOnlyTheMembersAssignedAndClaimedActiveChores_OnTheirDays()
    {
        var link = await CreateChoresLinkAsync(ClientA);

        var response = await _factory.CreateAnonymousClient().GetAsync(link.url);
        var calendar = await response.Content.ReadAsStringAsync();

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/calendar");
        response.Headers.CacheControl!.NoStore.Should().BeTrue();
        calendar.Should().Contain("X-WR-CALNAME:My Chores");
        Event(calendar, FlexibleMine).Should().Contain("DTSTART;VALUE=DATE:20260612").And.Contain("SUMMARY:Water plants");
        Event(calendar, OneOffClaimed).Should().Contain("DTSTART;VALUE=DATE:20260610").And.Contain("Claimed by you");
        calendar.Should().NotContain("Amy's laundry", "another member's chore is not mine");
        calendar.Should().NotContain("Finished one-off", "a done chore has no event");
        calendar.Should().NotContain("Pile chore", "an unassigned chore is nobody's");
        calendar.Should().NotContain("Household B private chore");
    }

    [Fact]
    public async Task AnOverdueChore_SitsOnToday_MarkedOverdue()
    {
        var link = await CreateChoresLinkAsync(ClientA);

        var calendar = await _factory.CreateAnonymousClient().GetStringAsync(link.url);

        Event(calendar, FixedOverdue).Should().Contain("DTSTART;VALUE=DATE:20260607").And.Contain("SUMMARY:Overdue: Take out recycling");
    }

    [Fact]
    public async Task AFlexibleChoresEvent_SlidesWithItsLastCompletion_UnderTheSameUid()
    {
        var link = await CreateChoresLinkAsync(ClientA);
        var anonymous = _factory.CreateAnonymousClient();
        Event(await anonymous.GetStringAsync(link.url), FlexibleMine).Should().Contain("DTSTART;VALUE=DATE:20260612");

        await using (var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync())
        {
            await context.Chores
                .Where(chore => chore.HouseholdId == ChoresWebAppFactory.HouseholdAId && chore.ChoreId == FlexibleMine)
                .ExecuteUpdateAsync(update => update.SetProperty(chore => chore.LastCompletedAt, Now));
        }

        Event(await anonymous.GetStringAsync(link.url), FlexibleMine)
            .Should().Contain("DTSTART;VALUE=DATE:20260614", "completed local 2026-06-07, every 7 days");
    }

    [Fact]
    public async Task ChoresAndMealLinks_NeverOpenEachOthersFeed()
    {
        var chores = await CreateChoresLinkAsync(ClientA);
        var mealResponse = await ClientA.PostAsync("/api/meal-plan/calendar-token", null);
        var meal = (await mealResponse.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        var anonymous = _factory.CreateAnonymousClient();

        var choresOnMeal = await anonymous.GetAsync("/api/calendar/meal-plan.ics?token=" + chores.token);
        var mealOnChores = await anonymous.GetAsync("/api/calendar/chores.ics?token=" + meal.token);

        choresOnMeal.StatusCode.Should().Be(HttpStatusCode.NotFound);
        mealOnChores.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await choresOnMeal.Content.ReadAsStringAsync()).Should().BeEmpty();
        (await mealOnChores.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task RotatingTheChoresLink_KillsOnlyThatLink()
    {
        var first = await CreateChoresLinkAsync(ClientA);
        var mealResponse = await ClientA.PostAsync("/api/meal-plan/calendar-token", null);
        var meal = (await mealResponse.Content.ReadFromJsonAsync<TokenResponse>(Json))!;
        var a2 = await CreateChoresLinkAsync(ClientA2);
        var anonymous = _factory.CreateAnonymousClient();

        var second = await CreateChoresLinkAsync(ClientA);

        (await anonymous.GetAsync(first.url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(second.url)).StatusCode.Should().Be(HttpStatusCode.OK);
        (await anonymous.GetAsync(meal.url)).StatusCode.Should().Be(HttpStatusCode.OK, "the meal link is another feed");
        var a2Feed = await anonymous.GetAsync(a2.url);
        a2Feed.StatusCode.Should().Be(HttpStatusCode.OK, "another member's chores link");
        (await a2Feed.Content.ReadAsStringAsync()).Should().Contain("Amy's laundry");

        (await ClientA.DeleteAsync("/api/chores/calendar-token")).StatusCode.Should().Be(HttpStatusCode.NoContent);
        (await anonymous.GetAsync(second.url)).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await anonymous.GetAsync(meal.url)).StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Status_IsTheCallersChoresLinkOnly()
    {
        (await ClientA.GetFromJsonAsync<TokenStatus>("/api/chores/calendar-token", Json))!.active.Should().BeFalse();
        await ClientA.PostAsync("/api/meal-plan/calendar-token", null);
        (await ClientA.GetFromJsonAsync<TokenStatus>("/api/chores/calendar-token", Json))!.active
            .Should().BeFalse("a meal link is not a chores link");

        await CreateChoresLinkAsync(ClientA);

        (await ClientA.GetFromJsonAsync<TokenStatus>("/api/chores/calendar-token", Json))!.active.Should().BeTrue();
        (await ClientA2.GetFromJsonAsync<TokenStatus>("/api/chores/calendar-token", Json))!.active.Should().BeFalse();
    }

    [Fact]
    public async Task ADisabledMembersChoresLink_IsAnEmpty404()
    {
        var a2 = await CreateChoresLinkAsync(ClientA2);
        await using (var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync())
        {
            await context.Users
                .Where(user => user.Id == ChoresWebAppFactory.UserA2Id)
                .ExecuteUpdateAsync(update => update.SetProperty(user => user.IsWhitelisted, false));
        }

        var response = await _factory.CreateAnonymousClient().GetAsync(a2.url);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task AMemberWithNoChores_GetsThePlaceholder()
    {
        var b = await CreateChoresLinkAsync(_factory.CreateClientAs(ChoresWebAppFactory.UserBEmail));

        var calendar = await _factory.CreateAnonymousClient().GetStringAsync(b.url);

        calendar.Should().Contain("SUMMARY:No chores assigned to you");
        calendar.Split("BEGIN:VEVENT").Length.Should().Be(2);
    }

    private static async Task<TokenResponse> CreateChoresLinkAsync(HttpClient client)
    {
        var response = await client.PostAsync("/api/chores/calendar-token", null);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var token = await response.Content.ReadFromJsonAsync<TokenResponse>(Json);
        token.Should().NotBeNull();
        token!.url.Should().Contain("/api/calendar/chores.ics?token=");
        return token;
    }

    /// <summary>The VEVENT block for one chore (by its stable UID), or an empty string.</summary>
    private static string Event(string calendar, int choreId)
    {
        var uid = $"UID:chore-{ChoresWebAppFactory.HouseholdAId}-{choreId}@family-coordination-app";
        foreach (var block in calendar.Split("BEGIN:VEVENT"))
        {
            if (block.Contains(uid, StringComparison.Ordinal)) return block;
        }
        return string.Empty;
    }

    private static Chore NewChore(
        int choreId,
        string name,
        RecurrenceMode mode,
        int assignee,
        AssignmentKind kind,
        int? intervalDays = null,
        DateOnly? anchorDate = null,
        ChoreDaysOfWeek? daysOfWeek = null,
        DateTime? lastCompletedAt = null,
        ChoreStatus status = ChoreStatus.Active) => new()
        {
            ChoreId = choreId,
            Name = name,
            RecurrenceMode = mode,
            IntervalDays = intervalDays,
            AnchorDate = anchorDate,
            DaysOfWeek = daysOfWeek,
            LastCompletedAt = lastCompletedAt,
            EffortTier = EffortTier.Standard,
            EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
            Status = status,
            EnteredByUserId = ChoresWebAppFactory.UserAId,
            AssigneeUserId = assignee,
            AssignmentKind = kind,
            ClaimedAt = Now.AddHours(-1),
            CreatedAt = Now.AddDays(-30),
        };
}
