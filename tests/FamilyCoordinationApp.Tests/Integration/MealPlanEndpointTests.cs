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
/// cross-household isolation invariant (a household-B caller never sees or mutates household-A's plan), and the
/// xmin concurrency contract — move / servings / remove refuse a stale token with a 409.
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
    private sealed record RecipeSummary(int recipeId, string name, string? imagePath, string recipeType, int? servings);
    private sealed record Entry(
        int mealPlanId, int entryId, string date, string mealType,
        RecipeSummary? recipe, string? customMealName, string? notes, int? servings, uint version);
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

    /// <summary>
    /// DELETE carries the entry's xmin token in the body, matching the chores DELETE (the house pattern).
    /// </summary>
    private static Task<HttpResponseMessage> DeleteEntryAsync(
        HttpClient client, int mealPlanId, int entryId, uint version) =>
        client.SendAsync(new HttpRequestMessage(HttpMethod.Delete, $"/api/meal-plan/entries/{mealPlanId}/{entryId}")
        {
            Content = JsonContent.Create(new { version }, options: Json)
        });

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

        var del = await DeleteEntryAsync(client, entry.mealPlanId, entry.entryId, entry.version);
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var board = await client.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-15", Json);
        board!.entries.Should().NotContain(e => e.entryId == entry.entryId);
    }

    [Fact]
    public async Task RemoveMissingEntry_IsRejected()
    {
        // No such entry ⇒ RemoveMealAsync throws ⇒ a clean 404 with a specific body rather than the generic
        // /api backfill.
        var del = await DeleteEntryAsync(ClientA, 999999, 999999, 1);

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
    public async Task SetServings_RoundTrips_ClearsToNull_AndRejectsNonPositive()
    {
        var entry = await AddEntryAsync(ClientA, new
        {
            date = "2026-06-29",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "Chili night",
            notes = (string?)null
        });
        entry.servings.Should().BeNull("a new entry is cooked as the recipe is written");

        var path = $"/api/meal-plan/entries/{entry.mealPlanId}/{entry.entryId}/servings";
        // Every accepted mutation bumps xmin, so the token has to be threaded from each response.
        var version = entry.version;

        var set = await ClientA.PatchAsJsonAsync(path, new { servings = (int?)8, version }, Json);
        set.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterSet = (await set.Content.ReadFromJsonAsync<Entry>(Json))!;
        afterSet.servings.Should().Be(8);
        afterSet.version.Should().NotBe(version, "an accepted write moves the row's xmin");
        version = afterSet.version;

        // It is a persisted field, not just an echo.
        var board = await ClientA.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-29", Json);
        board!.entries.Single(e => e.entryId == entry.entryId).servings.Should().Be(8);

        // null is the documented "back to the recipe as written" signal — it must NOT read as "unchanged".
        var cleared = await ClientA.PatchAsJsonAsync(path, new { servings = (int?)null, version }, Json);
        cleared.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterClear = (await cleared.Content.ReadFromJsonAsync<Entry>(Json))!;
        afterClear.servings.Should().BeNull();
        version = afterClear.version;

        // 0 would mean "cook none of it" and would be a divide-by-zero waiting to happen downstream.
        var zero = await ClientA.PatchAsJsonAsync(path, new { servings = 0, version }, Json);
        zero.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        (await zero.Content.ReadAsStringAsync()).Should().NotBeEmpty("every /api 4xx carries a body");

        // The value is a MULTIPLIER on every ingredient of the meal, and ShoppingListItem.Quantity is
        // decimal(10,2) — unbounded, this turns the next generate into a numeric-overflow 500.
        var huge = await ClientA.PatchAsJsonAsync(path, new { servings = 1_000_001, version }, Json);
        huge.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        // And the boundary itself is allowed, so the cap is a cap and not an off-by-one. A rejected write
        // must not have moved the token either — this only works if 0 and 1_000_001 truly changed nothing.
        var atCap = await ClientA.PatchAsJsonAsync(path, new { servings = 1000, version }, Json);
        atCap.StatusCode.Should().Be(HttpStatusCode.OK);
        version = (await atCap.Content.ReadFromJsonAsync<Entry>(Json))!.version;
        await ClientA.PatchAsJsonAsync(path, new { servings = (int?)null, version }, Json);
    }

    [Fact]
    public async Task EntryMutations_WithAStaleVersion_Are409_AndChangeNothing()
    {
        // The house 409 treatment, proved against real Postgres — the InMemory provider has no xmin, so the
        // service-level tests cannot reach this at all. Two writers read the same entry; the first wins.
        var entry = await AddEntryAsync(ClientA, new
        {
            date = "2026-07-13",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "Contested meal",
            notes = (string?)null
        });
        var staleVersion = entry.version;
        var servingsPath = $"/api/meal-plan/entries/{entry.mealPlanId}/{entry.entryId}/servings";

        // Writer one lands, moving xmin.
        var first = await ClientA.PatchAsJsonAsync(servingsPath, new { servings = 4, version = staleVersion }, Json);
        first.StatusCode.Should().Be(HttpStatusCode.OK);
        var freshVersion = (await first.Content.ReadFromJsonAsync<Entry>(Json))!.version;

        // Writer two is holding the token it read before that — every mutation must refuse it.
        var staleServings = await ClientA.PatchAsJsonAsync(servingsPath, new { servings = 99, version = staleVersion }, Json);
        staleServings.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await staleServings.Content.ReadAsStringAsync()).Should().NotBeEmpty("a 409 carries a body");

        var staleMove = await ClientA.PatchAsJsonAsync(
            $"/api/meal-plan/entries/{entry.mealPlanId}/{entry.entryId}",
            new { date = "2026-07-15", mealType = "lunch", version = staleVersion }, Json);
        staleMove.StatusCode.Should().Be(HttpStatusCode.Conflict);

        var staleDelete = await DeleteEntryAsync(ClientA, entry.mealPlanId, entry.entryId, staleVersion);
        staleDelete.StatusCode.Should().Be(HttpStatusCode.Conflict);

        // Nothing the stale writer attempted took effect: still 4 servings, still in its original slot.
        var board = await ClientA.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-07-13", Json);
        var live = board!.entries.Single(e => e.entryId == entry.entryId);
        live.servings.Should().Be(4);
        live.date.Should().Be("2026-07-13");
        live.mealType.Should().Be("dinner");

        // And the fresh token still works — the row is not wedged, only the stale writer is refused.
        var withFresh = await ClientA.PatchAsJsonAsync(servingsPath, new { servings = 6, version = freshVersion }, Json);
        withFresh.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task ASuccessfulMove_EchoesAReusableToken()
    {
        // The stale-token test proves refusal. This proves the other half of the contract: a mutation
        // hands back a token that WORKS for the next one. Without it, a client could only ever mutate an
        // entry once per board fetch and every second action would 409 — the check passing for the wrong
        // reason would look identical in the refusal test.
        var entry = await AddEntryAsync(ClientA, new
        {
            date = "2026-07-27",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "Chained mutations",
            notes = (string?)null
        });

        var moved = await ClientA.PatchAsJsonAsync(
            $"/api/meal-plan/entries/{entry.mealPlanId}/{entry.entryId}",
            new { date = "2026-07-29", mealType = "lunch", version = entry.version }, Json);
        moved.StatusCode.Should().Be(HttpStatusCode.OK);
        var afterMove = (await moved.Content.ReadFromJsonAsync<Entry>(Json))!;
        afterMove.version.Should().NotBe(entry.version, "the move moved the row's xmin");

        // Second mutation, straight off the echoed token — no refetch in between.
        var servings = await ClientA.PatchAsJsonAsync(
            $"/api/meal-plan/entries/{entry.mealPlanId}/{entry.entryId}/servings",
            new { servings = 3, version = afterMove.version }, Json);
        servings.StatusCode.Should().Be(HttpStatusCode.OK);

        // Third, chaining again — and this one is the DELETE body path.
        var afterServings = (await servings.Content.ReadFromJsonAsync<Entry>(Json))!;
        var removed = await DeleteEntryAsync(
            ClientA, entry.mealPlanId, entry.entryId, afterServings.version);
        removed.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    [Fact]
    public async Task AddingAMealAlreadyInTheSlot_DoesNotEraseItsNotes()
    {
        // Add folds into an existing entry when the same meal is already in that slot, and it is the ONE
        // entry write with no concurrency token. So it must not be able to destroy anything: an add that
        // omits notes previously blanked whatever another member had typed, with no conflict to notice.
        var first = await AddEntryAsync(ClientA, new
        {
            date = "2026-08-03",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "Shared dinner",
            notes = "Ask about the allergy"
        });
        first.notes.Should().Be("Ask about the allergy");

        var second = await AddEntryAsync(ClientA, new
        {
            date = "2026-08-03",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "Shared dinner",
            notes = (string?)null
        });
        second.entryId.Should().Be(first.entryId, "the duplicate folds into the existing entry");
        second.notes.Should().Be("Ask about the allergy", "an add that says nothing must not say 'blank'");

        // Supplying notes still updates them — the guard must not have frozen the field.
        var third = await AddEntryAsync(ClientA, new
        {
            date = "2026-08-03",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "Shared dinner",
            notes = "Bring the good knives"
        });
        third.notes.Should().Be("Bring the good knives");
    }

    [Fact]
    public async Task SetServings_IsHouseholdScoped()
    {
        var aEntry = await AddEntryAsync(ClientA, new
        {
            date = "2026-07-06",
            mealType = "dinner",
            recipeId = (int?)null,
            customMealName = "A's dinner",
            notes = (string?)null
        });

        // B scoping its own household finds nothing ⇒ a clean 404, and A's entry is untouched (M1).
        var resp = await ClientB.PatchAsJsonAsync(
            $"/api/meal-plan/entries/{aEntry.mealPlanId}/{aEntry.entryId}/servings",
            new { servings = 99, version = aEntry.version }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var aBoard = await ClientA.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-07-06", Json);
        aBoard!.entries.Single(e => e.entryId == aEntry.entryId).servings.Should().BeNull();
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

        // B cannot delete A's entry: RemoveMealAsync scopes to B's household ⇒ no match ⇒ a clean 404 with a
        // specific body rather than the generic /api backfill.
        var del = await DeleteEntryAsync(ClientB, aEntry.mealPlanId, aEntry.entryId, aEntry.version);
        del.StatusCode.Should().Be(HttpStatusCode.NotFound);

        // A's entry survives the cross-household delete attempt.
        var aBoardAfter = await ClientA.GetFromJsonAsync<Board>("/api/meal-plan/board?weekStart=2026-06-22", Json);
        aBoardAfter!.entries.Should().Contain(e => e.entryId == aEntry.entryId);
    }
}
