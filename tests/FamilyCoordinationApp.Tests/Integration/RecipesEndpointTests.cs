using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of the recipes-island endpoints through the real HTTP pipeline against real Postgres
/// (reuses <see cref="ChoresWebAppFactory"/>'s two-household seed). Proves: list/search, create→list→detail
/// round-trip, update (RecipeType change [D12] + wholesale ingredient replace, response projected from a fresh
/// load), soft delete, favorite toggle + the 404-not-500 guards, ingredient suggestions + parse + bulk-parse,
/// categories, image-upload validation, import duplicate detection, connected-household 403 + the connected
/// list/detail/copy happy path, draft round-trip, the 401 gate, and the M1 cross-household no-leak invariant.
/// Full-form PUT is xmin-guarded: a stale <c>version</c> token → 409 with a non-empty body (real Postgres is
/// what enforces the token — InMemory has no xmin). Tests use DISTINCT recipe names + scoped searches so the
/// class's shared database can't let them interfere; all 403 assertions use a never-connected household id.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class RecipesEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    // ── Wire shapes (camelCase via JsonSerializerDefaults.Web) ──────────────────────
    private sealed record ListItem(
        int recipeId, string name, string recipeType, string? imagePath, bool hasSourceUrl,
        string? createdByName, string? createdByPictureUrl, List<string> ingredientPreview, int ingredientCount);
    private sealed record RecipeList(List<ListItem> recipes, List<int> favoriteRecipeIds);
    private sealed record IngredientFull(
        int ingredientId, decimal? quantity, string? unit, string name, string category,
        string? notes, string? groupName, int sortOrder);
    private sealed record RecipeFull(
        int recipeId, uint version, string name, string recipeType, string? description, string? instructions,
        string instructionsHtml, string? imagePath, string? sourceUrl, int? prepTimeMinutes,
        int? cookTimeMinutes, int? servings, string? createdByName, string? createdByPictureUrl,
        string? sharedFromHouseholdName, List<IngredientFull> ingredients);
    private sealed record Parsed(
        decimal? quantity, string? unit, string name, string? notes, bool isComplete, string suggestedCategory);
    private sealed record ImportResult(
        bool success, int? recipeId, string? errorMessage, string? errorType,
        int? existingRecipeId, string? existingRecipeName);
    private sealed record Connected(int householdId, string householdName);
    private sealed record FavoriteResult(bool isFavorite);
    private sealed record ImagePathResult(string imagePath);
    private sealed record DraftIngredient(
        string name, decimal? quantity, string? unit, string category, string? notes, string? groupName, int sortOrder);
    private sealed record DraftWire(
        string name, string? description, string? instructions, string? imagePath, string? sourceUrl,
        int? servings, int? prepTimeMinutes, int? cookTimeMinutes, List<DraftIngredient> ingredients);

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    private static object WriteBody(
        string name, string type = "main", string? sourceUrl = null,
        IEnumerable<object>? ingredients = null, int? servings = 4, string? instructions = null,
        uint? version = null) => new
    {
        name,
        description = (string?)null,
        instructions,
        sourceUrl,
        servings,
        prepTimeMinutes = (int?)null,
        cookTimeMinutes = (int?)null,
        recipeType = type,
        imagePath = (string?)null,
        ingredients = ingredients ?? Array.Empty<object>(),
        version
    };

    private static object Ing(string name, double? qty = null, string? unit = null, string category = "Pantry",
        string? notes = null, string? groupName = null, int sortOrder = 0) => new
    {
        name,
        quantity = qty,
        unit,
        category,
        notes,
        groupName,
        sortOrder
    };

    private async Task<RecipeFull> CreateAsync(HttpClient client, object body)
    {
        var resp = await client.PostAsJsonAsync("/api/recipes", body, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        var full = await resp.Content.ReadFromJsonAsync<RecipeFull>(Json);
        full.Should().NotBeNull();
        return full!;
    }

    // ── List + detail ───────────────────────────────────────────────────────────

    [Fact]
    public async Task List_SearchForNonexistent_ReturnsEmpty()
    {
        var list = await ClientA.GetFromJsonAsync<RecipeList>(
            "/api/recipes?q=zzz-definitely-no-such-recipe-zzz", Json);

        list.Should().NotBeNull();
        list!.recipes.Should().BeEmpty();
        list.favoriteRecipeIds.Should().NotBeNull();
    }

    [Fact]
    public async Task Create_AppearsInList_AndDetailRoundTrips()
    {
        var created = await CreateAsync(ClientA, WriteBody(
            "RT Banana Bread", "dessert",
            ingredients: new[] { Ing("Bananas", 3, null, "Produce"), Ing("Flour", 2, "cup", "Pantry") },
            instructions: "Mash, mix, bake."));

        created.recipeId.Should().BeGreaterThan(0);
        created.recipeType.Should().Be("dessert");
        created.ingredients.Should().HaveCount(2);
        created.ingredients[0].name.Should().Be("Bananas");
        created.createdByName.Should().Be("Alice A"); // re-fetch loaded the author

        var list = await ClientA.GetFromJsonAsync<RecipeList>("/api/recipes?q=RT Banana", Json);
        list!.recipes.Should().Contain(r =>
            r.recipeId == created.recipeId && r.ingredientCount == 2 && r.ingredientPreview.Contains("Bananas"));

        var detail = await ClientA.GetFromJsonAsync<RecipeFull>($"/api/recipes/{created.recipeId}", Json);
        detail!.name.Should().Be("RT Banana Bread");
        detail.instructionsHtml.Should().Contain("Mash"); // markdown sanitized server-side
        detail.ingredients.Should().HaveCount(2);
    }

    [Fact]
    public async Task Search_MatchesIngredientName()
    {
        var created = await CreateAsync(ClientA, WriteBody(
            "Search By Ingredient Dish", "main",
            ingredients: new[] { Ing("Tarragon", 1, "tbsp", "Spices") }));

        // The server search matches ingredient names, not just the recipe name.
        var list = await ClientA.GetFromJsonAsync<RecipeList>("/api/recipes?q=Tarragon", Json);
        list!.recipes.Should().Contain(r => r.recipeId == created.recipeId);
    }

    [Fact]
    public async Task GetRecipe_Missing_Returns404()
    {
        var resp = await ClientA.GetAsync("/api/recipes/999999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Update_ChangesRecipeType_AndReplacesIngredients_ResponseReflectsThem()
    {
        var created = await CreateAsync(ClientA, WriteBody(
            "Update Target Dish", "main",
            ingredients: new[] { Ing("Old A", 1, "cup"), Ing("Old B", 2, "cup") }));

        var putBody = WriteBody(
            "Update Target Dish (edited)", "dessert",
            ingredients: new[] { Ing("New X", 3, "tbsp", "Spices"), Ing("New Y"), Ing("New Z", 1, "pinch") });

        var resp = await ClientA.PutAsJsonAsync($"/api/recipes/{created.recipeId}", putBody, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = await resp.Content.ReadFromJsonAsync<RecipeFull>(Json);

        // D12: RecipeType actually persists now (was silently dropped).
        updated!.recipeType.Should().Be("dessert");
        updated.name.Should().Be("Update Target Dish (edited)");
        // Council: the response is projected from a fresh load, so it shows the NEW ingredients, not the stale
        // pre-RemoveRange nav.
        updated.ingredients.Should().HaveCount(3);
        updated.ingredients.Select(i => i.name).Should().Equal("New X", "New Y", "New Z");
        updated.ingredients.Select(i => i.sortOrder).Should().Equal(0, 1, 2);

        // Persisted on a subsequent GET too.
        var detail = await ClientA.GetFromJsonAsync<RecipeFull>($"/api/recipes/{created.recipeId}", Json);
        detail!.recipeType.Should().Be("dessert");
        detail.ingredients.Should().HaveCount(3);
    }

    [Fact]
    public async Task Update_Missing_Returns404()
    {
        var resp = await ClientA.PutAsJsonAsync("/api/recipes/999999", WriteBody("Nope"), Json);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Optimistic concurrency (xmin — real Postgres enforces the token) ─────────────

    [Fact]
    public async Task Update_WithStaleVersion_Returns409WithBody_AndDoesNotOverwrite()
    {
        var created = await CreateAsync(ClientA, WriteBody("Concurrency Target Dish"));
        var staleVersion = created.version;
        staleVersion.Should().NotBe(0u, "real Postgres assigns a non-zero xmin to a committed row");

        // Writer 1 saves (advances xmin) — no token, legacy last-write-wins path still succeeds.
        var first = await ClientA.PutAsJsonAsync($"/api/recipes/{created.recipeId}",
            WriteBody("Concurrency Target Dish (writer 1)"), Json);
        first.StatusCode.Should().Be(HttpStatusCode.OK);

        // Writer 2 echoes the ORIGINAL (now stale) token → 409 with a NON-EMPTY body (a bare 4xx would
        // re-execute through the GET-only /not-found page and surface as a 405 on PUT).
        var second = await ClientA.PutAsJsonAsync($"/api/recipes/{created.recipeId}",
            WriteBody("Concurrency Target Dish (writer 2)", version: staleVersion), Json);
        second.StatusCode.Should().Be(HttpStatusCode.Conflict);
        var body = await second.Content.ReadAsStringAsync();
        body.Should().Contain("changed by someone else", "the 409 must carry a non-empty JSON message");

        // The stale writer did NOT overwrite writer 1.
        var detail = await ClientA.GetFromJsonAsync<RecipeFull>($"/api/recipes/{created.recipeId}", Json);
        detail!.name.Should().Be("Concurrency Target Dish (writer 1)");
    }

    [Fact]
    public async Task Update_WithCurrentVersion_Succeeds_AndAdvancesToken()
    {
        var created = await CreateAsync(ClientA, WriteBody("Concurrency Happy Dish"));

        var resp = await ClientA.PutAsJsonAsync($"/api/recipes/{created.recipeId}",
            WriteBody("Concurrency Happy Dish (edited)", version: created.version), Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);

        var updated = await resp.Content.ReadFromJsonAsync<RecipeFull>(Json);
        updated!.name.Should().Be("Concurrency Happy Dish (edited)");
        updated.version.Should().NotBe(created.version, "a successful save advances the xmin token");

        // The advanced token is immediately usable for the next save (reload-and-retry flow).
        var again = await ClientA.PutAsJsonAsync($"/api/recipes/{created.recipeId}",
            WriteBody("Concurrency Happy Dish (edited twice)", version: updated.version), Json);
        again.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Delete_SoftDeletes_GoneFromList()
    {
        var created = await CreateAsync(ClientA, WriteBody("Delete Me Dish"));

        var del = await ClientA.DeleteAsync($"/api/recipes/{created.recipeId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = await ClientA.GetFromJsonAsync<RecipeList>("/api/recipes?q=Delete Me Dish", Json);
        list!.recipes.Should().NotContain(r => r.recipeId == created.recipeId);

        // Soft-deleted ⇒ the global query filter hides it from detail too.
        var detail = await ClientA.GetAsync($"/api/recipes/{created.recipeId}");
        detail.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_Missing_Returns404()
    {
        var resp = await ClientA.DeleteAsync("/api/recipes/999999");
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Favorites ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Favorite_TogglesBothWays()
    {
        var created = await CreateAsync(ClientA, WriteBody("Favorite Toggle Dish"));

        var on = await ClientA.PostAsync($"/api/recipes/{created.recipeId}/favorite", null);
        on.StatusCode.Should().Be(HttpStatusCode.OK);
        (await on.Content.ReadFromJsonAsync<FavoriteResult>(Json))!.isFavorite.Should().BeTrue();

        var listOn = await ClientA.GetFromJsonAsync<RecipeList>("/api/recipes?q=Favorite Toggle", Json);
        listOn!.favoriteRecipeIds.Should().Contain(created.recipeId);

        var off = await ClientA.PostAsync($"/api/recipes/{created.recipeId}/favorite", null);
        (await off.Content.ReadFromJsonAsync<FavoriteResult>(Json))!.isFavorite.Should().BeFalse();

        var listOff = await ClientA.GetFromJsonAsync<RecipeList>("/api/recipes?q=Favorite Toggle", Json);
        listOff!.favoriteRecipeIds.Should().NotContain(created.recipeId);
    }

    [Fact]
    public async Task Favorite_Missing_Returns404_NotServerError()
    {
        // A bare ToggleFavorite would FK-violation/500 on a missing id; the existence pre-check makes it a 404.
        var resp = await ClientA.PostAsync("/api/recipes/999999/favorite", null);
        resp.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ── Ingredient entry helpers ──────────────────────────────────────────────────

    [Fact]
    public async Task IngredientSuggestions_ShortPrefix_ReturnsEmpty()
    {
        var results = await ClientA.GetFromJsonAsync<List<string>>("/api/recipes/ingredient-suggestions?prefix=a", Json);
        results.Should().NotBeNull().And.BeEmpty(); // service guards <2 chars
    }

    [Fact]
    public async Task IngredientSuggestions_FindsCreatedIngredient()
    {
        await CreateAsync(ClientA, WriteBody(
            "Suggestion Source Dish", ingredients: new[] { Ing("Cardamom", 1, "tsp", "Spices") }));

        var results = await ClientA.GetFromJsonAsync<List<string>>(
            "/api/recipes/ingredient-suggestions?prefix=Cardam", Json);
        results.Should().Contain("Cardamom");
    }

    [Fact]
    public async Task ParseIngredient_ParsesQuantityUnitName_AndInfersCategory()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/recipes/parse-ingredient", new { text = "2 cups flour" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var parsed = await resp.Content.ReadFromJsonAsync<Parsed>(Json);

        parsed!.quantity.Should().Be(2m);
        parsed.unit.Should().NotBeNullOrEmpty();
        parsed.name.ToLowerInvariant().Should().Contain("flour");
        parsed.isComplete.Should().BeTrue();
        parsed.suggestedCategory.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task ParseIngredient_Empty_Returns400()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/recipes/parse-ingredient", new { text = "   " }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ParseIngredients_Bulk_ParsesEachNonBlankLine()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/recipes/parse-ingredients",
            new { lines = new[] { "1 cup sugar", "", "  ", "2 eggs" } }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var parsed = await resp.Content.ReadFromJsonAsync<List<Parsed>>(Json);

        parsed.Should().HaveCount(2); // blanks skipped
        parsed!.Select(p => p.name.ToLowerInvariant()).Should().Contain(n => n.Contains("sugar"));
    }

    [Fact]
    public async Task Categories_ReturnsAList()
    {
        var resp = await ClientA.GetAsync("/api/recipes/categories");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var categories = await resp.Content.ReadFromJsonAsync<List<CategoryWire>>(Json);
        categories.Should().NotBeNull(); // possibly empty (no category seed) — proves the endpoint + auth
    }

    private sealed record CategoryWire(string name);

    // ── Images ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ImageUpload_NonImage_Returns400()
    {
        using var content = new MultipartFormDataContent();
        var fileContent = new ByteArrayContent("not an image"u8.ToArray());
        fileContent.Headers.ContentType = new MediaTypeHeaderValue("text/plain");
        content.Add(fileContent, "file", "notanimage.txt");

        var resp = await ClientA.PostAsync("/api/recipes/images", content);
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ImageUpload_NoFile_Returns400()
    {
        using var content = new MultipartFormDataContent();
        var resp = await ClientA.PostAsync("/api/recipes/images", content);
        resp.StatusCode.Should().BeOneOf(HttpStatusCode.BadRequest, HttpStatusCode.UnsupportedMediaType);
    }

    [Fact]
    public async Task ListImages_Returns200()
    {
        var resp = await ClientA.GetAsync("/api/recipes/images");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var images = await resp.Content.ReadFromJsonAsync<List<string>>(Json);
        images.Should().NotBeNull();
    }

    // ── Import (duplicate detection — no network) ──────────────────────────────────

    [Fact]
    public async Task Import_DuplicateUrl_ReturnsExisting()
    {
        const string url = "https://dup.test/recipe-already-here";
        var created = await CreateAsync(ClientA, WriteBody("Import Dup Source", sourceUrl: url));

        var resp = await ClientA.PostAsJsonAsync("/api/recipes/import", new { url, force = false }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<ImportResult>(Json);

        result!.success.Should().BeFalse();
        result.existingRecipeId.Should().Be(created.recipeId);
        result.existingRecipeName.Should().Be("Import Dup Source");
    }

    // ── Connected households ───────────────────────────────────────────────────────

    [Fact]
    public async Task ConnectedList_Unconnected_Returns403()
    {
        // Household 999 is never connected to A.
        var resp = await ClientA.GetAsync("/api/recipes/connected/999");
        resp.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Connected_ListDetailCopy_HappyPath()
    {
        await ConnectHouseholdsAsync();

        // B owns a recipe; A (connected) can list it (author stripped), read detail, and copy it.
        var bRecipe = await CreateAsync(ClientB, WriteBody(
            "Shared Casserole", "main", ingredients: new[] { Ing("Potatoes", 4, null, "Produce") }));

        var connectedList = await ClientA.GetFromJsonAsync<RecipeList>(
            $"/api/recipes/connected/{ChoresWebAppFactory.HouseholdBId}?q=Shared Casserole", Json);
        connectedList!.recipes.Should().Contain(r => r.recipeId == bRecipe.recipeId && r.createdByName == null);
        connectedList.favoriteRecipeIds.Should().BeEmpty();

        var detail = await ClientA.GetFromJsonAsync<RecipeFull>(
            $"/api/recipes/connected/{ChoresWebAppFactory.HouseholdBId}/{bRecipe.recipeId}", Json);
        detail!.name.Should().Be("Shared Casserole");
        detail.createdByName.Should().BeNull(); // privacy on connected reads

        var copyResp = await ClientA.PostAsync(
            $"/api/recipes/connected/{ChoresWebAppFactory.HouseholdBId}/{bRecipe.recipeId}/copy", null);
        copyResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var myList = await ClientA.GetFromJsonAsync<RecipeList>("/api/recipes?q=Shared Casserole", Json);
        myList!.recipes.Should().Contain(r => r.name == "Shared Casserole"); // now in A's own collection
    }

    // ── Drafts ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Draft_RoundTrip_SaveGetDelete()
    {
        // New-recipe draft (recipeId null). The body is FLAT: recipeId + the draft fields.
        var put = await ClientA.PutAsJsonAsync("/api/recipes/draft", new
        {
            recipeId = (int?)null,
            name = "Work In Progress",
            description = (string?)"a draft",
            instructions = (string?)null,
            imagePath = (string?)null,
            sourceUrl = (string?)null,
            servings = (int?)6,
            prepTimeMinutes = (int?)null,
            cookTimeMinutes = (int?)null,
            ingredients = new[] { new { name = "Eggs", quantity = (decimal?)2m, unit = (string?)null, category = "Dairy", notes = (string?)null, groupName = (string?)null, sortOrder = 0 } }
        }, Json);
        put.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var got = await ClientA.GetFromJsonAsync<DraftWire>("/api/recipes/draft", Json);
        got.Should().NotBeNull();
        got!.name.Should().Be("Work In Progress");
        got.servings.Should().Be(6);
        got.ingredients.Should().ContainSingle(i => i.name == "Eggs");

        var del = await ClientA.DeleteAsync("/api/recipes/draft");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var afterDelete = await ClientA.GetAsync("/api/recipes/draft");
        afterDelete.StatusCode.Should().Be(HttpStatusCode.NoContent); // no draft ⇒ 204
    }

    // ── Auth + M1 ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task List_Unauthenticated_Returns401()
    {
        var client = _factory.CreateAnonymousClient();
        var resp = await client.GetAsync("/api/recipes");
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CrossHousehold_BsRecipe_DoesNotLeakIntoAsList()
    {
        // B creates a uniquely-named recipe; it must NEVER appear in A's OWN list (M1) — regardless of any
        // connection (a connection only enables /connected/* reads, not leakage into the own-household list).
        var bRecipe = await CreateAsync(ClientB, WriteBody("B Private Secret Dish 4173"));

        var aList = await ClientA.GetFromJsonAsync<RecipeList>("/api/recipes?q=B Private Secret Dish 4173", Json);
        aList!.recipes.Should().NotContain(r => r.name == "B Private Secret Dish 4173");
        aList.recipes.Should().NotContain(r => r.recipeId == bRecipe.recipeId && r.name == "B Private Secret Dish 4173");
    }

    private async Task ConnectHouseholdsAsync()
    {
        using var scope = _factory.Services.CreateScope();
        var connections = scope.ServiceProvider.GetRequiredService<IHouseholdConnectionService>();
        if (await connections.AreHouseholdsConnectedAsync(ChoresWebAppFactory.HouseholdAId, ChoresWebAppFactory.HouseholdBId))
        {
            return;
        }

        var invite = await connections.GenerateInviteAsync(ChoresWebAppFactory.HouseholdBId, ChoresWebAppFactory.UserBId);
        var (success, _, error) = await connections.AcceptInviteAsync(
            invite.InviteCode, ChoresWebAppFactory.HouseholdAId, ChoresWebAppFactory.UserAId);
        success.Should().BeTrue($"the test households must connect (error: {error})");
    }
}
