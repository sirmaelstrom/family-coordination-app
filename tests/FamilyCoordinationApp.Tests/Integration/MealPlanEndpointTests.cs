using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of the meal-plan island endpoints through the real HTTP pipeline against real Postgres
/// (reuses <see cref="ChoresWebAppFactory"/>'s two-household seed). Proves: the read-only board (empty + populated),
/// add recipe / add custom round-trips + the add→board reflection, the XOR validation (400), remove (204) then
/// gone, a missing remove rejected (4xx), recipe search + quick-create, recipe detail, the 401 gate, and the M1
/// cross-household isolation invariant (a household-B caller never sees or mutates household-A's plan). Parity is
/// versionless — no xmin token anywhere on the wire.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class MealPlanEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // Wire shapes (subset — camelCase via JsonSerializerDefaults.Web).
    private sealed record RecipeSummary(int recipeId, string name, string? imagePath, string recipeType);
    private sealed record Entry(
        int mealPlanId, int entryId, string date, string mealType,
        RecipeSummary? recipe, string? customMealName, string? notes);
    private sealed record Board(string weekStartDate, int? mealPlanId, List<Entry> entries);
    private sealed record IngredientLine(decimal? quantity, string? unit, string name, string? notes, int sortOrder);
    private sealed record RecipeDetail(
        int recipeId, string name, string? imagePath, string recipeType,
        int? prepTimeMinutes, int? cookTimeMinutes, int? servings,
        string instructionsHtml, List<IngredientLine> ingredients);

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    // All weekStart values below are Mondays in June 2026 (the server snaps to Monday anyway); each test uses
    // a DISTINCT week so the class's shared database can't let tests interfere.
    private async Task<RecipeSummary> QuickCreateRecipeAsync(HttpClient client, string name, string type = "main")
    {
        var resp = await client.PostAsJsonAsync("/api/meal-plan/recipes", new { name, recipeType = type }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var summary = await resp.Content.ReadFromJsonAsync<RecipeSummary>(Json);
        summary.Should().NotBeNull();
        return summary!;
    }

    private async Task<Entry> AddEntryAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync("/api/meal-plan/entries", body, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var entry = await resp.Content.ReadFromJsonAsync<Entry>(Json);
        entry.Should().NotBeNull();
        return entry!;
    }

    [Fact]
    public async Task Board_EmptyWeek_ReturnsNullPlanAndNoEntries()
    {
        // A week never written to ⇒ no plan exists ⇒ a GET must NOT create one (mealPlanId null, entries []).
        var board = await ClientA.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-29", Json);

        board.Should().NotBeNull();
        board!.weekStartDate.Should().Be("2026-06-29");
        board.mealPlanId.Should().BeNull();
        board.entries.Should().BeEmpty();
    }

    [Fact]
    public async Task Board_SnapsWeekStartToMonday_ServerSide()
    {
        // Send a mid-week date (2026-06-03 is a Wednesday); the board must echo that week's Monday (2026-06-01).
        var board = await ClientA.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-03", Json);

        board.Should().NotBeNull();
        board!.weekStartDate.Should().Be("2026-06-01");
    }

    [Fact]
    public async Task AddRecipeEntry_RoundTrips_AndAppearsOnBoard()
    {
        var client = ClientA;
        var recipe = await QuickCreateRecipeAsync(client, "Test Pancakes", "breakfast");

        var entry = await AddEntryAsync(client, new
        {
            date = "2026-06-01",
            mealType = "breakfast",
            recipeId = recipe.recipeId,
            customMealName = (string?)null,
            notes = (string?)null
        });

        entry.recipe.Should().NotBeNull();
        entry.recipe!.recipeId.Should().Be(recipe.recipeId);
        entry.mealType.Should().Be("breakfast");
        entry.customMealName.Should().BeNull();

        var board = await client.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-01", Json);
        board!.mealPlanId.Should().NotBeNull();
        board.entries.Should().Contain(e =>
            e.entryId == entry.entryId && e.recipe != null && e.recipe.recipeId == recipe.recipeId);
    }

    [Fact]
    public async Task AddCustomMeal_RoundTrips()
    {
        var entry = await AddEntryAsync(ClientA, new
        {
            date = "2026-06-08",
            mealType = "lunch",
            recipeId = (int?)null,
            customMealName = "Eating out",
            notes = "birthday"
        });

        entry.recipe.Should().BeNull();
        entry.customMealName.Should().Be("Eating out");
        entry.notes.Should().Be("birthday");
        entry.mealType.Should().Be("lunch");
    }

    [Fact]
    public async Task AddEntry_BothRecipeAndCustom_Returns400()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/meal-plan/entries", new
        {
            date = "2026-06-01",
            mealType = "dinner",
            recipeId = 1,
            customMealName = "Both set",
            notes = (string?)null
        }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddEntry_NeitherRecipeNorCustom_Returns400()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/meal-plan/entries", new
        {
            date = "2026-06-01",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = (string?)null,
            notes = (string?)null
        }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task AddEntry_NonexistentRecipe_Returns404()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/meal-plan/entries", new
        {
            date = "2026-06-01",
            mealType = "dinner",
            recipeId = 999999,
            customMealName = (string?)null,
            notes = (string?)null
        }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task AddEntry_CrossHouseholdRecipe_Returns404_NotALeak()
    {
        // A recipe owned by household B must be unreachable from household A (M1): the household-scoped
        // GetRecipeAsync misses it, so the add is rejected up front with a clean 404 — never an FK-violation
        // 500, never a cross-tenant entry, and never an orphan MealPlan row.
        var bRecipe = await QuickCreateRecipeAsync(ClientB, "B's private recipe", "main");

        var resp = await ClientA.PostAsJsonAsync("/api/meal-plan/entries", new
        {
            date = "2026-06-01",
            mealType = "dinner",
            recipeId = bRecipe.recipeId,
            customMealName = (string?)null,
            notes = (string?)null
        }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RemoveEntry_DeletesIt_ThenGoneFromBoard()
    {
        var client = ClientA;
        var entry = await AddEntryAsync(client, new
        {
            date = "2026-06-15",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "To delete",
            notes = (string?)null
        });

        var del = await client.DeleteAsync($"/api/meal-plan/entries/{entry.mealPlanId}/{entry.entryId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var board = await client.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-15", Json);
        board!.entries.Should().NotContain(e => e.entryId == entry.entryId);
    }

    [Fact]
    public async Task RemoveMissingEntry_IsRejected()
    {
        // No such entry ⇒ RemoveMealAsync throws ⇒ a clean 404 (the non-empty body bypasses the app-global
        // status-code re-execute, so this is a true 404 — not the empty-body 405 the rewrite would produce).
        var del = await ClientA.DeleteAsync("/api/meal-plan/entries/999999/999999");

        del.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task RecipeSearch_FindsQuickCreatedRecipe()
    {
        var client = ClientA;
        var created = await QuickCreateRecipeAsync(client, "Zucchini Bread", "dessert");
        created.recipeId.Should().BeGreaterThan(0);
        created.recipeType.Should().Be("dessert");

        var results = await client.GetFromJsonAsync<List<RecipeSummary>>("/api/meal-plan/recipes?q=Zucchini", Json);
        results.Should().NotBeNull();
        results!.Should().Contain(r => r.recipeId == created.recipeId && r.name == "Zucchini Bread");
    }

    [Fact]
    public async Task RecipeDetail_ReturnsRecipe()
    {
        var client = ClientA;
        var created = await QuickCreateRecipeAsync(client, "Detail Recipe", "main");

        var detail = await client.GetFromJsonAsync<RecipeDetail>($"/api/meal-plan/recipes/{created.recipeId}", Json);

        detail.Should().NotBeNull();
        detail!.recipeId.Should().Be(created.recipeId);
        detail.name.Should().Be("Detail Recipe");
        detail.recipeType.Should().Be("main");
        // A quick-created recipe has no ingredients/instructions: [] + "" (ToSafeHtml(null) ⇒ string.Empty).
        detail.ingredients.Should().NotBeNull().And.BeEmpty();
        detail.instructionsHtml.Should().Be(string.Empty);
    }

    [Fact]
    public async Task RecipeDetail_Missing_IsRejected()
    {
        var resp = await ClientA.GetAsync("/api/meal-plan/recipes/999999");

        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Board_Unauthenticated_Returns401()
    {
        var client = _factory.CreateAnonymousClient();

        var resp = await client.GetAsync("/api/meal-plan/board?weekStart=2026-06-01");

        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CrossHousehold_Isolation_BCannotSeeOrDeleteAEntry()
    {
        // Household A plans a private meal in a week of its own.
        var aEntry = await AddEntryAsync(ClientA, new
        {
            date = "2026-06-22",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "A's private meal",
            notes = (string?)null
        });

        // Household B's board for the SAME week is its OWN (empty) plan — A's entry must not bleed in (M1).
        var bBoard = await ClientB.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-22", Json);
        bBoard!.entries.Should().NotContain(e =>
            e.entryId == aEntry.entryId && e.customMealName == "A's private meal");

        // B cannot delete A's entry: RemoveMealAsync scopes to B's household ⇒ no match ⇒ a clean 404 (the
        // non-empty body bypasses the status-code re-execute, so the contract is a deterministic 404).
        var del = await ClientB.DeleteAsync($"/api/meal-plan/entries/{aEntry.mealPlanId}/{aEntry.entryId}");
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A's entry survives the cross-household delete attempt.
        var aBoardAfter = await ClientA.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-22", Json);
        aBoardAfter!.entries.Should().Contain(e => e.entryId == aEntry.entryId);
    }
}
