using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// One household clock (quest 5197d71c): the dashboard, the meal plan and chores read the same "today". The
/// fixture's household runs on the default America/Chicago zone. At 04:30Z on Monday 2026-06-08 it is still 23:30
/// on Sunday 2026-06-07 in Central time, so a server that dates by UTC, or by its own process-local
/// <see cref="DateTime.Today"/>, lands on a different day (and a different meal-plan week) than chores does.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class HouseholdClockTests(PostgresContainerFixture postgres)
{
    private static readonly DateTime LateSundayCentral = new(2026, 6, 8, 4, 30, 0, DateTimeKind.Utc);
    private static readonly DateOnly CentralToday = new(2026, 6, 7);
    private static readonly DateOnly CentralWeekMonday = new(2026, 6, 1);

    [Fact]
    public async Task At_2330_Central_dashboard_and_meal_plan_today_agree_with_chores()
    {
        await using var factory = new ChoresWebAppFactory(postgres);
        await factory.EnsureSeededAsync();
        factory.Clock.SetUtcNow(LateSundayCentral);
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add(ChoresWebAppFactory.TestUserHeader, ChoresWebAppFactory.UserAEmail);

        // Chores' today, observed through its own rule: a snooze date must be after today.
        var snoozeToday = await client.PatchAsJsonAsync($"/api/chores/{ChoresWebAppFactory.PileChoreAId}/snooze",
            new { until = CentralToday, version = 0u });
        snoozeToday.StatusCode.Should().Be(HttpStatusCode.BadRequest, "chores' today is 2026-06-07, so it is not in the future");
        var snoozeTomorrow = await client.PatchAsJsonAsync($"/api/chores/{ChoresWebAppFactory.PileChoreAId}/snooze",
            new { until = CentralToday.AddDays(1), version = 0u });
        snoozeTomorrow.StatusCode.Should().NotBe(HttpStatusCode.BadRequest,
            "2026-06-08 is after chores' today; the stale version may answer 409, but the date passed validation");

        var dashboard = await client.GetFromJsonAsync<JsonElement>("/api/dashboard");
        DateOnly.Parse(dashboard.GetProperty("today").GetString()!).Should().Be(CentralToday,
            "the dashboard's today is the household's today, the same date chores uses");

        var board = await client.GetFromJsonAsync<JsonElement>("/api/meal-plan/board");
        DateOnly.Parse(board.GetProperty("weekStartDate").GetString()!).Should().Be(CentralWeekMonday,
            "the meal plan's default week is the week of the household's today");
    }
}
