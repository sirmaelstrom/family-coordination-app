using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The shopping-list group's HTTP/isolation suite (quest 9101a410 — this was the only endpoint group
/// without one). Tenant isolation exercises the FOLDED resolver (UserContextResolver — the group's
/// private fork was deleted by the same quest); the archive-lifecycle and delta cases are the
/// HTTP-boundary coverage the PR #101 council round deferred here by name. Data is created through
/// the API itself (house pattern — see RecipesEndpointTests); tests share one database, so every
/// assertion is containment-shaped, never a count of all lists.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ShoppingListEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    // ── Wire shapes (mirrors of the DTOs; drift is caught by the M9 pin, these are for reading) ──
    private sealed record Summary(int id, string name, bool isFavorite, int itemCount, int uncheckedCount);
    private sealed record ArchivedSummary(
        int id, string name, bool isFavorite, int itemCount, int uncheckedCount,
        DateTime createdAt, bool hasMealPlan);
    private sealed record Item(
        int id, string name, decimal? quantity, string? unit, string category, bool isChecked,
        DateTime? checkedAt, int sortOrder, string? addedByName, string? addedByInitials,
        string? addedByPictureUrl, uint version);
    private sealed record ListDto(
        int id, string name, bool isFavorite, bool isArchived, bool hasMealPlan, List<Item> items);

    // ── Helpers ──────────────────────────────────────────────────────────────

    private async Task<Summary> CreateListAsync(HttpClient client, string name)
    {
        var resp = await client.PostAsJsonAsync("/api/shopping-lists/", new { name }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<Summary>(Json))!;
    }

    private async Task<Item> AddItemAsync(HttpClient client, int listId, string name, decimal? quantity = 1m)
    {
        var resp = await client.PostAsJsonAsync(
            $"/api/shopping-lists/{listId}/items", new { name, quantity, category = "Pantry" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<Item>(Json))!;
    }

    private async Task<ListDto> GetListAsync(HttpClient client, int listId)
    {
        var resp = await client.GetAsync($"/api/shopping-lists/{listId}");
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        return (await resp.Content.ReadFromJsonAsync<ListDto>(Json))!;
    }

    private async Task ArchiveAsync(HttpClient client, int listId) =>
        (await client.PostAsync($"/api/shopping-lists/{listId}/actions/archive", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

    /// <summary>Recipe + meal-plan entry + generate: the only path that produces GENERATED items.</summary>
    private async Task<Summary> GenerateLinkedListAsync(HttpClient client, string listName, string ingredientName)
    {
        var recipeResp = await client.PostAsJsonAsync("/api/recipes", new
        {
            name = $"Recipe for {listName}",
            description = (string?)null,
            instructions = "Cook.",
            sourceUrl = (string?)null,
            servings = 4,
            prepTimeMinutes = (int?)null,
            cookTimeMinutes = (int?)null,
            recipeType = "main",
            imagePath = (string?)null,
            ingredients = new[]
            {
                new { name = ingredientName, quantity = (decimal?)2m, unit = (string?)"cup",
                      category = "Pantry", notes = (string?)null, groupName = (string?)null, sortOrder = 1 },
            },
            version = (int?)null,
        }, Json);
        recipeResp.StatusCode.Should().Be(HttpStatusCode.Created);
        var recipeId = (await recipeResp.Content.ReadFromJsonAsync<JsonElement>(Json)).GetProperty("recipeId").GetInt32();

        var entryResp = await client.PostAsJsonAsync("/api/meal-plan/entries", new
        {
            date = "2026-09-08",
            mealType = "dinner",
            recipeId,
            customMealName = (string?)null,
            notes = (string?)null,
        }, Json);
        entryResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var genResp = await client.PostAsJsonAsync("/api/shopping-lists/actions/generate-from-meal-plan", new
        {
            startDate = "2026-09-07",
            endDate = "2026-09-13",
            name = listName,
        }, Json);
        genResp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await genResp.Content.ReadFromJsonAsync<Summary>(Json))!;
    }

    private static async Task AssertJsonError(HttpResponseMessage resp, HttpStatusCode expected)
    {
        resp.StatusCode.Should().Be(expected);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/json");
        var body = await resp.Content.ReadAsStringAsync();
        JsonDocument.Parse(body).RootElement.TryGetProperty("message", out var msg).Should().BeTrue();
        msg.GetString().Should().NotBeNullOrWhiteSpace();
    }

    // ── Auth + isolation (the folded resolver's contract) ────────────────────

    [Fact]
    public async Task AnonymousRequest_Answers401Json()
    {
        var resp = await _factory.CreateAnonymousClient().GetAsync("/api/shopping-lists/");

        await AssertJsonError(resp, HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CrossHousehold_ListsAreInvisible()
    {
        var bList = await CreateListAsync(ClientB, "B secret groceries");

        var aActive = await ClientA.GetFromJsonAsync<List<Summary>>("/api/shopping-lists/", Json);
        aActive!.Should().NotContain(l => l.id == bList.id && l.name == "B secret groceries");

        (await ClientA.GetAsync($"/api/shopping-lists/{bList.id}")).StatusCode
            .Should().NotBe(HttpStatusCode.OK, "household A must never read household B's list");

        await ArchiveAsync(ClientB, bList.id);
        var aArchived = await ClientA.GetFromJsonAsync<List<ArchivedSummary>>("/api/shopping-lists/archived", Json);
        aArchived!.Should().NotContain(l => l.name == "B secret groceries");
    }

    [Fact]
    public async Task CrossHousehold_MutationsAnswer404()
    {
        var aList = await CreateListAsync(ClientA, "A list for isolation");
        var aItem = await AddItemAsync(ClientA, aList.id, "milk");

        (await ClientB.PatchAsync($"/api/shopping-lists/{aList.id}/items/{aItem.id}",
            JsonContent.Create(new { isChecked = true }, options: Json))).StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ClientB.PostAsync($"/api/shopping-lists/{aList.id}/actions/restore", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ClientB.PostAsync($"/api/shopping-lists/{aList.id}/actions/regenerate", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await ClientB.DeleteAsync($"/api/shopping-lists/{aList.id}"))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);

        // And the list is untouched.
        (await GetListAsync(ClientA, aList.id)).items.Should().ContainSingle(i => i.name == "milk");
    }

    // ── Item lifecycle ───────────────────────────────────────────────────────

    [Fact]
    public async Task CheckingAnItem_SetsAndClearsCheckedAt()
    {
        var list = await CreateListAsync(ClientA, "Check lifecycle");
        var item = await AddItemAsync(ClientA, list.id, "eggs");

        var check = await ClientA.PatchAsync($"/api/shopping-lists/{list.id}/items/{item.id}",
            JsonContent.Create(new { isChecked = true }, options: Json));
        check.StatusCode.Should().Be(HttpStatusCode.OK);
        var checkedItem = (await check.Content.ReadFromJsonAsync<Item>(Json))!;
        checkedItem.isChecked.Should().BeTrue();
        checkedItem.checkedAt.Should().NotBeNull();

        var uncheck = await ClientA.PatchAsync($"/api/shopping-lists/{list.id}/items/{item.id}",
            JsonContent.Create(new { isChecked = false }, options: Json));
        (await uncheck.Content.ReadFromJsonAsync<Item>(Json))!.checkedAt.Should().BeNull();
    }

    // ── The generated-item delta path at the HTTP boundary (PR #101 council debt) ──

    [Fact]
    public async Task GeneratedItem_QuantityEdit_SurvivesRegenerate()
    {
        var summary = await GenerateLinkedListAsync(ClientA, "Delta week", "delta flour");
        var list = await GetListAsync(ClientA, summary.id);
        list.hasMealPlan.Should().BeTrue();
        var flour = list.items.Single(i => i.name == "delta flour");
        flour.quantity.Should().Be(2m, "the recipe says 2 cup and nothing scaled it");

        // The user bumps 2 → 5 (delta +3), and checks it off.
        (await ClientA.PatchAsync($"/api/shopping-lists/{summary.id}/items/{flour.id}",
            JsonContent.Create(new { quantity = 5m, isChecked = true }, options: Json)))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var regen = await ClientA.PostAsync($"/api/shopping-lists/{summary.id}/actions/regenerate", null);
        regen.StatusCode.Should().Be(HttpStatusCode.OK);
        var regenerated = (await regen.Content.ReadFromJsonAsync<ListDto>(Json))!;

        var freshFlour = regenerated.items.Single(i => i.name == "delta flour");
        freshFlour.quantity.Should().Be(5m, "fresh consolidated 2 + the persisted delta 3");
        freshFlour.isChecked.Should().BeTrue("checked state carries across a regenerate");
    }

    [Fact]
    public async Task GeneratedItem_UnitEdit_ClearsTheDelta()
    {
        var summary = await GenerateLinkedListAsync(ClientA, "Unit-clear week", "unit sugar");
        var list = await GetListAsync(ClientA, summary.id);
        var sugar = list.items.Single(i => i.name == "unit sugar");

        // Edit the quantity (delta +3), then change the unit — the delta must die with the old unit.
        (await ClientA.PatchAsync($"/api/shopping-lists/{summary.id}/items/{sugar.id}",
            JsonContent.Create(new { quantity = 5m }, options: Json))).StatusCode.Should().Be(HttpStatusCode.OK);
        (await ClientA.PatchAsync($"/api/shopping-lists/{summary.id}/items/{sugar.id}",
            JsonContent.Create(new { unit = "g" }, options: Json))).StatusCode.Should().Be(HttpStatusCode.OK);

        var regen = await ClientA.PostAsync($"/api/shopping-lists/{summary.id}/actions/regenerate", null);
        var regenerated = (await regen.Content.ReadFromJsonAsync<ListDto>(Json))!;

        regenerated.items.Single(i => i.name == "unit sugar").quantity
            .Should().Be(2m, "the unit change invalidated the delta, so regenerate resets to the generator's number");
    }

    [Fact]
    public async Task ManualItem_IsUntouchedByRegenerate()
    {
        var summary = await GenerateLinkedListAsync(ClientA, "Manual-survives week", "gen rice");
        var manual = await AddItemAsync(ClientA, summary.id, "duct tape", 3m);

        (await ClientA.PostAsync($"/api/shopping-lists/{summary.id}/actions/regenerate", null))
            .StatusCode.Should().Be(HttpStatusCode.OK);

        var after = await GetListAsync(ClientA, summary.id);
        var tape = after.items.Single(i => i.name == "duct tape");
        tape.id.Should().Be(manual.id, "manual rows are not rebuilt");
        tape.quantity.Should().Be(3m);
    }

    [Fact]
    public async Task Regenerate_OnAnUnlinkedList_Answers409()
    {
        var list = await CreateListAsync(ClientA, "Unlinked");

        await AssertJsonError(
            await ClientA.PostAsync($"/api/shopping-lists/{list.id}/actions/regenerate", null),
            HttpStatusCode.Conflict);
    }

    // ── Archive lifecycle (the past-lists surface) ───────────────────────────

    [Fact]
    public async Task Archive_MovesTheListToThePastSurface()
    {
        var summary = await GenerateLinkedListAsync(ClientA, "Archive lifecycle week", "arch beans");
        await ArchiveAsync(ClientA, summary.id);

        (await ClientA.GetFromJsonAsync<List<Summary>>("/api/shopping-lists/", Json))!
            .Should().NotContain(l => l.id == summary.id);
        (await ClientA.GetAsync($"/api/shopping-lists/{summary.id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "the active detail read is active-only");

        var archived = (await ClientA.GetFromJsonAsync<List<ArchivedSummary>>("/api/shopping-lists/archived", Json))!;
        var row = archived.Single(l => l.id == summary.id);
        row.hasMealPlan.Should().BeTrue();
        row.createdAt.Should().BeAfter(DateTime.UtcNow.AddDays(-1));

        var detail = await ClientA.GetAsync($"/api/shopping-lists/archived/{summary.id}");
        detail.StatusCode.Should().Be(HttpStatusCode.OK);
        (await detail.Content.ReadFromJsonAsync<ListDto>(Json))!.isArchived.Should().BeTrue();
    }

    [Fact]
    public async Task ArchivedDetailRead_IsArchivedOnly()
    {
        var active = await CreateListAsync(ClientA, "Still active");

        (await ClientA.GetAsync($"/api/shopping-lists/archived/{active.id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "an ACTIVE list has no archived detail");
    }

    [Fact]
    public async Task ArchivedList_OrdinaryMutations_Answer409()
    {
        var list = await CreateListAsync(ClientA, "Frozen list");
        var item = await AddItemAsync(ClientA, list.id, "relic");
        await ArchiveAsync(ClientA, list.id);

        await AssertJsonError(await ClientA.PatchAsync($"/api/shopping-lists/{list.id}/items/{item.id}",
            JsonContent.Create(new { isChecked = true }, options: Json)), HttpStatusCode.Conflict);
        await AssertJsonError(await ClientA.PostAsJsonAsync($"/api/shopping-lists/{list.id}/items",
            new { name = "new thing" }, Json), HttpStatusCode.Conflict);
        await AssertJsonError(await ClientA.DeleteAsync($"/api/shopping-lists/{list.id}/items/{item.id}"),
            HttpStatusCode.Conflict);
        await AssertJsonError(await ClientA.PostAsync($"/api/shopping-lists/{list.id}/actions/clear-checked", null),
            HttpStatusCode.Conflict);
        await AssertJsonError(await ClientA.PostAsJsonAsync($"/api/shopping-lists/{list.id}/items/sort-orders",
            new { updates = new[] { new { itemId = item.id, sortOrder = 2, category = "Pantry" } } }, Json),
            HttpStatusCode.Conflict);
        await AssertJsonError(await ClientA.PostAsJsonAsync($"/api/shopping-lists/{list.id}/actions/rename",
            new { name = "New name" }, Json), HttpStatusCode.Conflict);

        // The read-only surface still reads, and the frozen item is intact.
        var detail = await ClientA.GetAsync($"/api/shopping-lists/archived/{list.id}");
        (await detail.Content.ReadFromJsonAsync<ListDto>(Json))!
            .items.Should().ContainSingle(i => i.name == "relic" && !i.isChecked);
    }

    [Fact]
    public async Task ArchivedList_ToggleFavorite_IsAllowed_AndFiltersApply()
    {
        var list = await CreateListAsync(ClientA, "Favorite past list");
        await ArchiveAsync(ClientA, list.id);

        (await ClientA.PostAsync($"/api/shopping-lists/{list.id}/actions/toggle-favorite", null))
            .StatusCode.Should().Be(HttpStatusCode.OK, "the past surface's favorites filter needs the toggle");

        var favorites = (await ClientA.GetFromJsonAsync<List<ArchivedSummary>>(
            "/api/shopping-lists/archived?favoritesOnly=true", Json))!;
        favorites.Should().Contain(l => l.id == list.id);
        favorites.Should().OnlyContain(l => l.isFavorite);
    }

    [Fact]
    public async Task Restore_ReopensTheList()
    {
        var list = await CreateListAsync(ClientA, "Round trip");
        await ArchiveAsync(ClientA, list.id);

        (await ClientA.PostAsync($"/api/shopping-lists/{list.id}/actions/restore", null))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ClientA.GetFromJsonAsync<List<Summary>>("/api/shopping-lists/", Json))!
            .Should().Contain(l => l.id == list.id);
        // Restoring an already-active list is a miss, not a no-op.
        (await ClientA.PostAsync($"/api/shopping-lists/{list.id}/actions/restore", null))
            .StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Delete_IsArchivedOnly_ThenPermanent()
    {
        var list = await CreateListAsync(ClientA, "Doomed list");

        await AssertJsonError(await ClientA.DeleteAsync($"/api/shopping-lists/{list.id}"),
            HttpStatusCode.Conflict);

        await ArchiveAsync(ClientA, list.id);
        (await ClientA.DeleteAsync($"/api/shopping-lists/{list.id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        (await ClientA.GetAsync($"/api/shopping-lists/archived/{list.id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound, "delete is permanent — gone from the past surface too");
        (await ClientA.GetAsync($"/api/shopping-lists/{list.id}")).StatusCode
            .Should().Be(HttpStatusCode.NotFound);
    }

    // ── Miss mapping (challenge residual: these answered 500 before the throw alignment) ──

    [Fact]
    public async Task MissingResources_AnswerCleanJson404_Never500()
    {
        var list = await CreateListAsync(ClientA, "Miss mapping");

        await AssertJsonError(await ClientA.DeleteAsync($"/api/shopping-lists/{list.id}/items/999999"),
            HttpStatusCode.NotFound);
        await AssertJsonError(await ClientA.PostAsync("/api/shopping-lists/999999/actions/toggle-favorite", null),
            HttpStatusCode.NotFound);
        await AssertJsonError(await ClientA.PostAsync("/api/shopping-lists/999999/actions/archive", null),
            HttpStatusCode.NotFound);
    }
}
