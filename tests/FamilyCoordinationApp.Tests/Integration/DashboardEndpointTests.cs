using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of the dashboard island endpoint through the real HTTP pipeline against real Postgres
/// (reuses <see cref="ChoresWebAppFactory"/>'s two-household seed). Proves: the aggregate read composes the
/// greeting + household name + chore counts (via the server-side <c>ChoreHomeStats</c> reducer) + shopping
/// summary (summed across active lists, archived excluded) + today's meals (today-only, recipe vs custom name),
/// the empty-household zero case creates NO rows (read-only), the 401 gate, and the M1 cross-household isolation
/// invariant (a household-B caller's dashboard never reflects household-A's data). Read-only — no writes anywhere.
///
/// <para>Shopping/meal seeding is confined to household A so household B stays pristine for the empty +
/// read-only assertions regardless of test execution order (the shared per-class DB has no method ordering).
/// The chore-snooze guard on up-for-grabs is unit-tested separately in <c>ChoreHomeStatsTests</c>; here we assert
/// the reducer is wired through the real board.</para>
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class DashboardEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // Wire shapes (camelCase via JsonSerializerDefaults.Web). `@checked` escapes the C# keyword; the JSON key is "checked".
    private sealed record ChoreSummary(int activeTotal, int overdue, int dueToday, int upForGrabs);
    private sealed record ShoppingSummary(int remaining, int @checked, int total);
    private sealed record Meal(string mealType, string displayName);
    private sealed record Dashboard(
        string greetingName, string householdName, string today,
        ChoreSummary chores, ShoppingSummary shopping, List<Meal> todaysMeals);

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    private static readonly DateOnly Today = DateOnly.FromDateTime(DateTime.Today);
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd");

    // ─── Seeding helpers (real API — parity-correct) ─────────────────────────────

    private async Task<int> CreateListAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/shopping-lists", new { name }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        return doc.GetProperty("id").GetInt32();
    }

    private async Task<int> AddItemAsync(HttpClient client, int listId, string name)
    {
        var resp = await client.PostAsJsonAsync($"/api/shopping-lists/{listId}/items", new { name }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        return doc.GetProperty("id").GetInt32();
    }

    private async Task CheckItemAsync(HttpClient client, int listId, int itemId)
    {
        var resp = await client.PatchAsJsonAsync(
            $"/api/shopping-lists/{listId}/items/{itemId}", new { isChecked = true }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    private async Task ArchiveListAsync(HttpClient client, int listId)
    {
        var resp = await client.PostAsync($"/api/shopping-lists/{listId}/actions/archive", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private async Task<int> QuickCreateRecipeAsync(HttpClient client, string name, string type = "main")
    {
        var resp = await client.PostAsJsonAsync("/api/meal-plan/recipes", new { name, recipeType = type }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var doc = await resp.Content.ReadFromJsonAsync<JsonElement>(Json);
        return doc.GetProperty("recipeId").GetInt32();
    }

    private async Task AddMealAsync(HttpClient client, DateOnly date, string mealType, int? recipeId, string? customMealName)
    {
        var resp = await client.PostAsJsonAsync("/api/meal-plan/entries", new
        {
            date = Iso(date),
            mealType,
            recipeId,
            customMealName,
            notes = (string?)null
        }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    // ─── Tests ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Dashboard_Unauthenticated_Returns401()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await client.GetAsync("/api/dashboard");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Dashboard_EmptyHouseholdB_ZeroShoppingAndNoMeals_CreatesNoRows()
    {
        // Household B has one seeded chore but NO shopping lists and NO meal plans, and nothing seeds them
        // (all shopping/meal seeding is confined to household A) — so the empty branches are exercised cleanly.
        var resp = await ClientB.GetAsync("/api/dashboard");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var dash = (await resp.Content.ReadFromJsonAsync<Dashboard>(Json))!;

        dash.householdName.Should().Be("Household B");
        dash.greetingName.Should().Be(ChoresWebAppFactory.UserBEmail); // no GivenName claim ⇒ Name (= email) first word
        dash.today.Should().Be(Iso(Today));

        dash.chores.activeTotal.Should().Be(1, "household B has exactly one seeded active chore");
        dash.chores.upForGrabs.Should().Be(1, "B's chore is unassigned (AssignmentKind.None) and not snoozed");

        dash.shopping.remaining.Should().Be(0);
        dash.shopping.@checked.Should().Be(0);
        dash.shopping.total.Should().Be(0);
        dash.todaysMeals.Should().BeEmpty();

        // Read-only guarantee: the GET must not have created a MealPlan or ShoppingList row for B.
        using var scope = _factory.Services.CreateScope();
        var dbFactory = scope.ServiceProvider.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var db = await dbFactory.CreateDbContextAsync();
        (await db.MealPlans.AnyAsync(mp => mp.HouseholdId == ChoresWebAppFactory.HouseholdBId))
            .Should().BeFalse("a dashboard read must never create a meal plan");
        (await db.ShoppingLists.AnyAsync(l => l.HouseholdId == ChoresWebAppFactory.HouseholdBId))
            .Should().BeFalse("a dashboard read must never create a shopping list");
    }

    [Fact]
    public async Task Dashboard_PopulatedHouseholdA_AggregatesChoresShoppingMeals_AndIsolatesFromB()
    {
        var client = ClientA;

        // ── Shopping: two active lists (with a check) + one archived list (must be excluded) ──
        var list1 = await CreateListAsync(client, "Groceries");
        var m1 = await AddItemAsync(client, list1, "Milk");
        await AddItemAsync(client, list1, "Eggs");
        await AddItemAsync(client, list1, "Bread");
        await CheckItemAsync(client, list1, m1);          // list1: 2 remaining, 1 checked

        var list2 = await CreateListAsync(client, "Hardware");
        await AddItemAsync(client, list2, "Nails");        // list2: 1 remaining, 0 checked

        var archived = await CreateListAsync(client, "Old list");
        await AddItemAsync(client, archived, "Should not count");
        await ArchiveListAsync(client, archived);          // excluded from active sums

        // ── Meals: today recipe + today custom + tomorrow recipe (tomorrow must be excluded) ──
        var dinnerRecipe = await QuickCreateRecipeAsync(client, "Today Dinner Recipe", "main");
        await AddMealAsync(client, Today, "dinner", dinnerRecipe, null);
        await AddMealAsync(client, Today, "lunch", null, "Today Custom Lunch");
        var tomorrowRecipe = await QuickCreateRecipeAsync(client, "Tomorrow Breakfast", "breakfast");
        await AddMealAsync(client, Today.AddDays(1), "breakfast", tomorrowRecipe, null);

        // ── Assert A's aggregate ──
        var dash = (await client.GetFromJsonAsync<Dashboard>("/api/dashboard", Json))!;

        dash.householdName.Should().Be("Household A");
        dash.greetingName.Should().Be(ChoresWebAppFactory.UserAEmail);
        dash.today.Should().Be(Iso(Today));

        dash.chores.activeTotal.Should().Be(1, "household A has exactly one seeded active chore");
        dash.chores.upForGrabs.Should().Be(1, "the seeded pile chore is unassigned and not snoozed");

        dash.shopping.remaining.Should().Be(3, "list1 (2) + list2 (1); the archived list is excluded");
        dash.shopping.@checked.Should().Be(1, "one item checked on list1");
        dash.shopping.total.Should().Be(4, "remaining + checked across the two active lists");

        dash.todaysMeals.Should().HaveCount(2, "tomorrow's entry is excluded; today has a lunch + a dinner");
        dash.todaysMeals[0].mealType.Should().Be("lunch");       // ordered by MealType (Lunch < Dinner)
        dash.todaysMeals[0].displayName.Should().Be("Today Custom Lunch");
        dash.todaysMeals[1].mealType.Should().Be("dinner");
        dash.todaysMeals[1].displayName.Should().Be("Today Dinner Recipe");
        dash.todaysMeals.Should().NotContain(m => m.displayName == "Tomorrow Breakfast");

        // ── M1 isolation: household B's dashboard reflects NONE of A's shopping/meals ──
        var bDash = (await ClientB.GetFromJsonAsync<Dashboard>("/api/dashboard", Json))!;
        bDash.householdName.Should().Be("Household B");
        bDash.shopping.total.Should().Be(0, "A's lists must never bleed into B (M1)");
        bDash.todaysMeals.Should().BeEmpty("A's meals must never bleed into B (M1)");
    }
}
