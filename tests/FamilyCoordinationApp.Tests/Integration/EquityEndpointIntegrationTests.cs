using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// V3 — the equity distribution endpoint (<c>GET /api/chores/equity</c>) on real Postgres + the booted host.
/// Proves household isolation (each caller sums only their OWN household's effort-weighted completions, M1),
/// the window allowlist (bogus → 400), and that an anonymous caller is rejected with no data leak (the /api
/// status-code branch preserves any 4xx — never a 200/leak).
/// <para>Aggregation is evaluated against the factory's FIXED clock, at whose Mon–Sun week the seeded
/// mid-week completions fall — so the totals are deterministic.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class EquityEndpointIntegrationTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private sealed record MemberShare(int userId, string displayName, int points, int completions, double sharePct);
    private sealed record MemberPlanning(
        int userId, string displayName, int choresSetUp, int recipesAdded, int listItemsCurated, int handOffs,
        int mealsPlanned);
    private sealed record Equity(
        string window, int totalPoints, int totalCompletions, double equalSharePct,
        int fallingBehindCount, int upForGrabsCount, List<MemberShare> members, List<MemberPlanning> planning);
    private sealed record BoardChore(int id, uint version);
    private sealed record Board(List<BoardChore> chores);

    [Fact]
    public async Task Equity_ExcludesSnoozedChore_FromAttentionCounts()
    {
        // V11 (equity surface). Household A's only chore (Flexible, never-completed, unclaimed) is both
        // falling-behind (DueToday) and up-for-grabs. Snoozing it must drop BOTH counts to zero. The pre-snooze
        // counts are the cleared-snooze control.
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var before = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);
        before!.fallingBehindCount.Should().Be(1, "the unclaimed, never-completed pile chore reads DueToday");
        before.upForGrabsCount.Should().Be(1, "and it is unassigned");

        // Snooze it via the dedicated PATCH endpoint (reads the current version from the board).
        var board = await client.GetFromJsonAsync<Board>("/api/chores/board", Json);
        var pile = board!.chores.Single(c => c.id == ChoresWebAppFactory.PileChoreAId);
        var snooze = await client.PatchAsync($"/api/chores/{ChoresWebAppFactory.PileChoreAId}/snooze",
            JsonContent.Create(new { days = 5, version = pile.version }, options: Json));
        snooze.StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);
        after!.fallingBehindCount.Should().Be(0, "a snoozed chore is excluded from falling-behind");
        after.upForGrabsCount.Should().Be(0, "a snoozed chore is excluded from up-for-grabs");
    }

    [Fact]
    public async Task Equity_AsUserA_SumsOnlyHouseholdA()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);

        equity.Should().NotBeNull();
        equity!.window.Should().Be("week");
        // Household A seed: Alice 2×Standard (4 pts) + Amy 1×Quick (1 pt) = 3 completions / 5 pts.
        equity.totalCompletions.Should().Be(3, "only household A's three in-week completions count");
        equity.totalPoints.Should().Be(5, "only household A's effort points — no bleed from B");
        equity.members.Should().HaveCount(2, "household A has two members (Alice, Amy)");
        equity.members.Should().Contain(m => m.displayName == "Alice A" && m.points == 4 && m.completions == 2);
        equity.members.Should().Contain(m => m.displayName == "Amy A" && m.points == 1 && m.completions == 1);
        equity.members.Should().NotContain(m => m.displayName == "Bob B", "household B's member must not appear");
    }

    [Fact]
    public async Task Equity_AsUserB_SumsOnlyHouseholdB()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);

        equity.Should().NotBeNull();
        // Household B seed: Bob 1×BigJob (3 pts) = 1 completion / 3 pts.
        equity!.totalCompletions.Should().Be(1, "only household B's single in-week completion counts");
        equity.totalPoints.Should().Be(3, "only household B's effort points — no bleed from A");
        equity.members.Should().ContainSingle().Which.displayName.Should().Be("Bob B");
        equity.members.Should().NotContain(
            m => m.displayName == "Alice A" || m.displayName == "Amy A", "household A's members must not appear");
    }

    [Fact]
    public async Task Equity_DefaultWindow_IsWeek()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        // No window query → defaults to week.
        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity", Json);

        equity.Should().NotBeNull();
        equity!.window.Should().Be("week");
        equity.totalCompletions.Should().Be(3);
    }

    [Fact]
    public async Task Equity_WindowAll_SumsAllTime()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var equity = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=all", Json);

        equity.Should().NotBeNull();
        equity!.window.Should().Be("all");
        // Same three completions are also within the all-time window (no lower bound).
        equity.totalCompletions.Should().Be(3);
        equity.totalPoints.Should().Be(5);
    }

    [Fact]
    public async Task Equity_PlanningIsAllTime_ByteIdenticalAcrossWindows()
    {
        // V5: planning lanes are ALL-TIME — the `window` param governs only the physical lane. The
        // `planning` array must be byte-identical between ?window=week and ?window=all. Compare the raw
        // serialized `planning` node so any drift (count, ordering, key) fails the assertion.
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var weekJson = await client.GetStringAsync("/api/chores/equity?window=week");
        var allJson = await client.GetStringAsync("/api/chores/equity?window=all");

        var weekPlanning = JsonDocument.Parse(weekJson).RootElement.GetProperty("planning").GetRawText();
        var allPlanning = JsonDocument.Parse(allJson).RootElement.GetProperty("planning").GetRawText();

        weekPlanning.Should().Be(allPlanning, "planning is all-time and must not vary with the equity window");

        // Sanity: the planning array carries one row per household-A member (Alice, Amy).
        var planning = await client.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);
        planning!.planning.Should().HaveCount(2, "one planning row per household-A member");
    }

    [Fact]
    public async Task Equity_MealsPlannedLane_CountsAttributedEntries_HouseholdScoped()
    {
        // F1: the fifth planning lane, proven through the real HTTP pipeline. Entries created via the
        // endpoint are stamped with the caller (mealsPlanned credit); a directly-inserted null-author row
        // (the pre-migration shape) credits nobody; household B's entries never reach A's planning rows.
        var alice = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
        var amy = _factory.CreateClientAs(ChoresWebAppFactory.UserA2Email);
        var bob = _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

        var monday = new DateOnly(2026, 1, 5);

        (await alice.PostAsJsonAsync("/api/meal-plan/entries",
            new { date = monday, mealType = "dinner", customMealName = "Tacos" }, Json)).EnsureSuccessStatusCode();
        (await alice.PostAsJsonAsync("/api/meal-plan/entries",
            new { date = monday.AddDays(1), mealType = "dinner", customMealName = "Curry" }, Json)).EnsureSuccessStatusCode();
        (await amy.PostAsJsonAsync("/api/meal-plan/entries",
            new { date = monday, mealType = "lunch", customMealName = "Soup" }, Json)).EnsureSuccessStatusCode();
        (await bob.PostAsJsonAsync("/api/meal-plan/entries",
            new { date = monday, mealType = "dinner", customMealName = "Pizza" }, Json)).EnsureSuccessStatusCode();

        // A pre-migration-shaped row: same household + plan, no author. Must credit nobody.
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using (var db = await dbFactory.CreateDbContextAsync())
        {
            var planId = await db.MealPlanEntries
                .Where(e => e.HouseholdId == ChoresWebAppFactory.HouseholdAId)
                .Select(e => e.MealPlanId)
                .FirstAsync();
            var nextEntryId = await db.MealPlanEntries
                .Where(e => e.HouseholdId == ChoresWebAppFactory.HouseholdAId && e.MealPlanId == planId)
                .MaxAsync(e => e.EntryId) + 1;
            db.MealPlanEntries.Add(new MealPlanEntry
            {
                HouseholdId = ChoresWebAppFactory.HouseholdAId,
                MealPlanId = planId,
                EntryId = nextEntryId,
                Date = monday.AddDays(2),
                MealType = MealType.Dinner,
                CustomMealName = "Mystery leftovers",
                CreatedByUserId = null,
            });
            await db.SaveChangesAsync();
        }

        // The lane is all-time, so week and all must agree on the same counts.
        foreach (var window in new[] { "week", "all" })
        {
            var equity = await alice.GetFromJsonAsync<Equity>($"/api/chores/equity?window={window}", Json);
            equity!.planning.Single(p => p.displayName == "Alice A").mealsPlanned
                .Should().Be(2, $"Alice created two entries via the endpoint (window={window})");
            equity.planning.Single(p => p.displayName == "Amy A").mealsPlanned
                .Should().Be(1, $"Amy created one, and the null-author row credits nobody (window={window})");
            equity.planning.Should().NotContain(p => p.displayName == "Bob B",
                "household B never appears in A's planning rows");
        }

        var equityB = await bob.GetFromJsonAsync<Equity>("/api/chores/equity?window=week", Json);
        equityB!.planning.Should().ContainSingle().Which.mealsPlanned
            .Should().Be(1, "Bob's own entry counts in his household only — no bleed from A's entries");
    }

    [Fact]
    public async Task Equity_BogusWindow_Returns400()
    {
        var client = _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

        var resp = await client.GetAsync("/api/chores/equity?window=fortnight");

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an unknown window is rejected, never coerced");
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace("the 400 carries a JSON message");
    }

    [Fact]
    public async Task Equity_Anonymous_IsRejected_NoLeak()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await client.GetAsync("/api/chores/equity?window=week");

        // 4xx (401 challenge from RequireAuthorization) — the load-bearing property is it is NOT a 200 and leaks
        // no equity data.
        ((int)resp.StatusCode).Should().BeInRange(400, 499, "an anonymous caller must be rejected");
        resp.StatusCode.Should().NotBe(HttpStatusCode.OK);

        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotContain("totalPoints", "no equity payload may leak to an unauthenticated caller");
        body.Should().NotContain("Alice A");
        body.Should().NotContain("Bob B");
    }
}
