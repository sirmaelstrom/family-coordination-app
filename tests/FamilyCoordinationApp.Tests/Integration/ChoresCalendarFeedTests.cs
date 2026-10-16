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
    private const int Snoozed = 106;        // A assigned; Flexible 7d, done 2026-06-05 (→ 06-12), snoozed until 2026-06-20
    private const int NeverDone = 107;      // A assigned; Flexible 5d, never completed → due today, not overdue
    private const int OneOffDone = 108;     // A assigned; OneOff with a completion but still Active → no next-due
    private const int UnderSpecSnoozed = 109; // A assigned; Fixed with no cadence, snoozed until 2026-06-15
    private const int ClaimedRecurring = 111; // A claimed; Flexible 2d → completing returns it to the pile
    private const int ForeignAssignedToA = 110; // household B's chore whose assignee id is A's (only the Tenant filter excludes it)

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
                daysOfWeek: ChoreDaysOfWeek.Monday, lastCompletedAt: new DateTime(2026, 5, 25, 17, 0, 0, DateTimeKind.Utc)),
            NewChore(Snoozed, "Clean gutters", RecurrenceMode.Flexible, ChoresWebAppFactory.UserAId, AssignmentKind.Assigned,
                intervalDays: 7, lastCompletedAt: new DateTime(2026, 6, 5, 17, 0, 0, DateTimeKind.Utc), snoozedUntil: new DateOnly(2026, 6, 20)),
            NewChore(NeverDone, "Wipe counters", RecurrenceMode.Flexible, ChoresWebAppFactory.UserAId, AssignmentKind.Assigned,
                intervalDays: 5),
            NewChore(OneOffDone, "Return library books", RecurrenceMode.OneOff, ChoresWebAppFactory.UserAId, AssignmentKind.Assigned,
                anchorDate: new DateOnly(2026, 6, 9), lastCompletedAt: new DateTime(2026, 6, 6, 17, 0, 0, DateTimeKind.Utc)),
            NewChore(UnderSpecSnoozed, "Someday task", RecurrenceMode.Fixed, ChoresWebAppFactory.UserAId, AssignmentKind.Assigned,
                snoozedUntil: new DateOnly(2026, 6, 15)),
            NewChore(ClaimedRecurring, "Feed the cat", RecurrenceMode.Flexible, ChoresWebAppFactory.UserAId, AssignmentKind.Claimed,
                intervalDays: 2));
        await context.SaveChangesAsync();

        await using var foreign = await new PostgresDbContextFactory(
            _factory.ConnectionString, TestTenancy.Caller(ChoresWebAppFactory.HouseholdBId, ChoresWebAppFactory.UserBId)).CreateDbContextAsync();
        var forged = NewChore(ForeignAssignedToA, "Household B chore naming A", RecurrenceMode.Flexible, ChoresWebAppFactory.UserAId,
            AssignmentKind.Assigned, intervalDays: 3);
        forged.EnteredByUserId = ChoresWebAppFactory.UserBId;
        foreign.Chores.Add(forged);
        await foreign.SaveChangesAsync();
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
        calendar.Should().NotContain("Household B chore naming A", "only the Tenant filter excludes this one: its assignee id is A's");
    }

    [Fact]
    public async Task Dates_FollowTheCalculator_ForSnoozeNeverDoneAndSatisfiedOneOffs()
    {
        var link = await CreateChoresLinkAsync(ClientA);

        var calendar = await _factory.CreateAnonymousClient().GetStringAsync(link.url);

        Event(calendar, Snoozed).Should().Contain("DTSTART;VALUE=DATE:20260620", "the snooze floor is later than the natural 06-12");
        Event(calendar, NeverDone).Should().Contain("DTSTART;VALUE=DATE:20260607").And.Contain("SUMMARY:Wipe counters",
            "a never-done Flexible chore is due today, not overdue");
        Event(calendar, OneOffDone).Should().BeEmpty("a satisfied one-off has no next-due");
        Event(calendar, UnderSpecSnoozed).Should().Contain("DTSTART;VALUE=DATE:20260615",
            "no cadence, but the calculator answers the snooze floor; the feed keeps parity with the board");
    }

    [Fact]
    public async Task CompletingThroughTheApi_SlidesAnAssignedChore_AndDropsAClaimedOneBackToThePile()
    {
        var link = await CreateChoresLinkAsync(ClientA);
        var anonymous = _factory.CreateAnonymousClient();

        await CompleteAsync(FlexibleMine);
        await CompleteAsync(ClaimedRecurring);

        var calendar = await anonymous.GetStringAsync(link.url);
        Event(calendar, FlexibleMine).Should().Contain("DTSTART;VALUE=DATE:20260614", "completed local 2026-06-07, every 7 days; still assigned");
        Event(calendar, ClaimedRecurring).Should().BeEmpty("completing a claimed recurring chore returns it to the pile");
    }

    [Fact]
    public async Task AMultiPersonChore_IsInTheFeedOfEachMemberOnItsRoster_UntilTheirPartIsDone()
    {
        // Created through the API, so the roster lives where production keeps it: participation events, with
        // no assignee on the chore (slim-review on fca#157).
        var createResp = await ClientA.PostAsJsonAsync("/api/chores/", new
        {
            name = "Move the couch",
            recurrenceMode = "flexible",
            intervalDays = 30,
            effortTier = "standard",
            requiredCount = 2,
            assignedUserIds = new[] { ChoresWebAppFactory.UserAId, ChoresWebAppFactory.UserA2Id },
        }, Json);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var choreId = (await createResp.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("id").GetInt32();
        var a = await CreateChoresLinkAsync(ClientA);
        var a2 = await CreateChoresLinkAsync(ClientA2);
        var anonymous = _factory.CreateAnonymousClient();

        Event(await anonymous.GetStringAsync(a.url), choreId).Should().Contain("SUMMARY:Move the couch").And.Contain("on the roster");
        Event(await anonymous.GetStringAsync(a2.url), choreId).Should().Contain("SUMMARY:Move the couch");

        await CompleteAsync(choreId); // A does their part; the occurrence still needs A2

        Event(await anonymous.GetStringAsync(a.url), choreId).Should().BeEmpty("A's part of this occurrence is done");
        Event(await anonymous.GetStringAsync(a2.url), choreId).Should().Contain("SUMMARY:Move the couch");
    }

    private async Task CompleteAsync(int choreId)
    {
        uint version;
        await using (var context = await new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContextAsync())
        {
            version = await context.Chores
                .Where(chore => chore.HouseholdId == ChoresWebAppFactory.HouseholdAId && chore.ChoreId == choreId)
                .Select(chore => chore.Version)
                .SingleAsync();
        }
        var response = await ClientA.PostAsJsonAsync($"/api/chores/{choreId}/complete", new { note = (string?)null, photoPath = (string?)null, version }, Json);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
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
    public async Task AUserlessChoresToken_NeverResolves()
    {
        // No route writes one (a chores link is always the caller's); only the meal plan had a shared link.
        var token = Convert.ToBase64String(System.Security.Cryptography.RandomNumberGenerator.GetBytes(32))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');
        await using (var context = await new PostgresDbContextFactory(
                         _factory.ConnectionString, TestTenancy.Caller(ChoresWebAppFactory.HouseholdAId)).CreateDbContextAsync())
        {
            context.HouseholdCalendarTokens.Add(new HouseholdCalendarToken
            {
                UserId = null,
                Feed = CalendarFeed.Chores,
                TokenHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(token))),
                CreatedAt = Now,
            });
            await context.SaveChangesAsync();
        }

        var response = await _factory.CreateAnonymousClient().GetAsync("/api/calendar/chores.ics?token=" + token);

        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await response.Content.ReadAsStringAsync()).Should().BeEmpty();
        // The endpoint also refuses a token with no user, so check the resolve itself: it is the one statement that
        // bypasses the Tenant filter, and it must not hand any caller a userless non-meal token.
        var service = new FamilyCoordinationApp.Services.Calendar.HouseholdCalendarTokenService(
            new PostgresDbContextFactory(_factory.ConnectionString), Services.TestClocks.System);
        (await service.ResolveActiveAsync(token, CalendarFeed.Chores)).Should().BeNull();
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
        ChoreStatus status = ChoreStatus.Active,
        DateOnly? snoozedUntil = null) => new()
        {
            SnoozedUntil = snoozedUntil,
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
