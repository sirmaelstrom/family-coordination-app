using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of the Settings island A endpoints (<c>/api/settings/categories</c> + <c>/api/settings/members</c>)
/// through the real HTTP pipeline against real Postgres (reuses <see cref="ChoresWebAppFactory"/>'s two-household
/// seed — household A has alice + amy, household B has bob). Each test method gets its OWN freshly-seeded database
/// (the factory provisions a per-instance DB), so mutations are isolated. Proves: the category CRUD/reorder/restore
/// lifecycle, the in-use probe, the member list + add outcomes + the self/cross-household guards, the 401 gate, and
/// the M1 cross-household isolation invariant (a household-A caller can neither read nor mutate household-B rows).
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class SettingsEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();
    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);
    private HttpClient ClientB => _factory.CreateClientAs(ChoresWebAppFactory.UserBEmail);

    // Wire shapes (camelCase via JsonSerializerDefaults.Web).
    private sealed record CategoryDto(int categoryId, string name, string? iconEmoji, string color, bool isDefault, int sortOrder, string? deletedAt);
    private sealed record CategoryList(List<CategoryDto> active, List<CategoryDto> deleted);
    private sealed record MemberDto(int userId, string email, string? displayName, bool isWhitelisted);
    private sealed record MemberList(int currentUserId, List<MemberDto> members);
    private sealed record MemberAction(MemberDto member, string outcome);

    private const string CategoriesUrl = "/api/settings/categories";
    private const string MembersUrl = "/api/settings/members";

    private async Task<CategoryDto> CreateCategoryAsync(HttpClient client, string name, string color = "#123456")
    {
        var resp = await client.PostAsJsonAsync(CategoriesUrl, new { name, iconEmoji = (string?)null, color }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<CategoryDto>(Json))!;
    }

    // ─── Categories ───────────────────────────────────────────────────────────────

    [Fact]
    public async Task Categories_Unauthenticated_Returns401()
    {
        var resp = await _factory.CreateAnonymousClient().GetAsync(CategoriesUrl);
        resp.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Categories_FullLifecycle_CreateUpdateDeleteRestoreReorder()
    {
        var client = ClientA;

        // Create two.
        var produce = await CreateCategoryAsync(client, "Produce");
        var dairy = await CreateCategoryAsync(client, "Dairy");

        var afterCreate = (await client.GetFromJsonAsync<CategoryList>(CategoriesUrl, Json))!;
        afterCreate.active.Select(c => c.name).Should().Contain(new[] { "Produce", "Dairy" });
        afterCreate.deleted.Should().BeEmpty();

        // Update.
        var upd = await client.PutAsJsonAsync($"{CategoriesUrl}/{produce.categoryId}",
            new { name = "Fresh Produce", iconEmoji = "leafy", color = "#00AA00" }, Json);
        upd.StatusCode.Should().Be(HttpStatusCode.OK);
        var updated = (await upd.Content.ReadFromJsonAsync<CategoryDto>(Json))!;
        updated.name.Should().Be("Fresh Produce");
        updated.iconEmoji.Should().Be("leafy");

        // Reorder: dairy first, produce second.
        var reorder = await client.PutAsJsonAsync($"{CategoriesUrl}/sort-order",
            new { orderedIds = new[] { dairy.categoryId, produce.categoryId } }, Json);
        reorder.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterReorder = (await client.GetFromJsonAsync<CategoryList>(CategoriesUrl, Json))!;
        afterReorder.active.OrderBy(c => c.sortOrder).Select(c => c.categoryId)
            .Should().ContainInOrder(dairy.categoryId, produce.categoryId);

        // Soft delete dairy → moves to the deleted list.
        var del = await client.DeleteAsync($"{CategoriesUrl}/{dairy.categoryId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterDelete = (await client.GetFromJsonAsync<CategoryList>(CategoriesUrl, Json))!;
        afterDelete.active.Select(c => c.categoryId).Should().NotContain(dairy.categoryId);
        afterDelete.deleted.Select(c => c.categoryId).Should().Contain(dairy.categoryId);
        afterDelete.deleted.Single(c => c.categoryId == dairy.categoryId).deletedAt.Should().NotBeNull();

        // Restore dairy → back to active.
        var restore = await client.PostAsync($"{CategoriesUrl}/{dairy.categoryId}/restore", content: null);
        restore.StatusCode.Should().Be(HttpStatusCode.NoContent);
        var afterRestore = (await client.GetFromJsonAsync<CategoryList>(CategoriesUrl, Json))!;
        afterRestore.active.Select(c => c.categoryId).Should().Contain(dairy.categoryId);
        afterRestore.deleted.Should().BeEmpty();
    }

    [Fact]
    public async Task Categories_InUse_FalseWhenNoIngredients_And404ForMissing()
    {
        var client = ClientA;
        var cat = await CreateCategoryAsync(client, "Spices");

        var inUse = await client.GetFromJsonAsync<JsonElement>($"{CategoriesUrl}/{cat.categoryId}/in-use", Json);
        inUse.GetProperty("inUse").GetBoolean().Should().BeFalse();

        var missing = await client.GetAsync($"{CategoriesUrl}/99999/in-use");
        missing.StatusCode.Should().Be(HttpStatusCode.NotFound);
        (await missing.Content.ReadAsStringAsync()).Should().NotBeNullOrEmpty("4xx must carry a non-empty body");
    }

    [Fact]
    public async Task Categories_CrossHousehold_IsIsolated_AndMutationsAre404()
    {
        // A creates a category; B must never see it, nor be able to update/delete it (M1).
        var aCat = await CreateCategoryAsync(ClientA, "A Only");

        var bList = (await ClientB.GetFromJsonAsync<CategoryList>(CategoriesUrl, Json))!;
        bList.active.Select(c => c.name).Should().NotContain("A Only");

        var bUpdate = await ClientB.PutAsJsonAsync($"{CategoriesUrl}/{aCat.categoryId}",
            new { name = "Hijack", iconEmoji = (string?)null, color = "#000000" }, Json);
        bUpdate.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var bDelete = await ClientB.DeleteAsync($"{CategoriesUrl}/{aCat.categoryId}");
        bDelete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    // ─── Members ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Members_List_ReturnsHouseholdMembers_WithCurrentUserId()
    {
        var list = (await ClientA.GetFromJsonAsync<MemberList>(MembersUrl, Json))!;
        list.currentUserId.Should().Be(ChoresWebAppFactory.UserAId);
        list.members.Select(m => m.email).Should().BeEquivalentTo(
            new[] { ChoresWebAppFactory.UserAEmail, ChoresWebAppFactory.UserA2Email });
        list.members.Should().NotContain(m => m.email == ChoresWebAppFactory.UserBEmail, "B's user must not leak (M1)");
    }

    [Fact]
    public async Task Members_Add_Created_AlreadyActive_And_CrossHousehold409()
    {
        var client = ClientA;

        // New email → created.
        var addResp = await client.PostAsJsonAsync(MembersUrl, new { email = "newbie@household-a.test" }, Json);
        addResp.StatusCode.Should().Be(HttpStatusCode.OK);
        var action = (await addResp.Content.ReadFromJsonAsync<MemberAction>(Json))!;
        action.outcome.Should().Be("created");
        action.member.displayName.Should().Be("newbie");

        // Already active → alreadyActive (NOT an error — review R-A1).
        var dupe = await client.PostAsJsonAsync(MembersUrl, new { email = ChoresWebAppFactory.UserA2Email }, Json);
        dupe.StatusCode.Should().Be(HttpStatusCode.OK);
        (await dupe.Content.ReadFromJsonAsync<MemberAction>(Json))!.outcome.Should().Be("alreadyActive");

        // Email belonging to another household → 409.
        var collision = await client.PostAsJsonAsync(MembersUrl, new { email = ChoresWebAppFactory.UserBEmail }, Json);
        collision.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await collision.Content.ReadAsStringAsync()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Members_ToggleAndReenable_RoundTrips()
    {
        var client = ClientA;
        // Disable amy (alice stays active so it's not the last-active guard).
        var off = await client.PutAsJsonAsync($"{MembersUrl}/{ChoresWebAppFactory.UserA2Id}", new { isWhitelisted = false }, Json);
        off.StatusCode.Should().Be(HttpStatusCode.OK);
        (await off.Content.ReadFromJsonAsync<MemberDto>(Json))!.isWhitelisted.Should().BeFalse();

        var on = await client.PutAsJsonAsync($"{MembersUrl}/{ChoresWebAppFactory.UserA2Id}", new { isWhitelisted = true }, Json);
        on.StatusCode.Should().Be(HttpStatusCode.OK);
        (await on.Content.ReadFromJsonAsync<MemberDto>(Json))!.isWhitelisted.Should().BeTrue();
    }

    [Fact]
    public async Task Members_SelfGuards_ToggleAndDelete_AreRejected()
    {
        var client = ClientA; // alice = UserAId

        var toggleSelf = await client.PutAsJsonAsync($"{MembersUrl}/{ChoresWebAppFactory.UserAId}", new { isWhitelisted = false }, Json);
        toggleSelf.StatusCode.Should().Be(HttpStatusCode.BadRequest);

        var deleteSelf = await client.DeleteAsync($"{MembersUrl}/{ChoresWebAppFactory.UserAId}");
        deleteSelf.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task Members_Delete_NonSelf_FreshMember_RemovesThem()
    {
        var client = ClientA;

        // Add a fresh member (no chore completions / FK references), then delete them.
        var add = await client.PostAsJsonAsync(MembersUrl, new { email = "deleteme@household-a.test" }, Json);
        add.StatusCode.Should().Be(HttpStatusCode.OK);
        var newId = (await add.Content.ReadFromJsonAsync<MemberAction>(Json))!.member.userId;

        var del = await client.DeleteAsync($"{MembersUrl}/{newId}");
        del.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var list = (await client.GetFromJsonAsync<MemberList>(MembersUrl, Json))!;
        list.members.Should().NotContain(m => m.userId == newId);
    }

    [Fact]
    public async Task Members_Delete_MemberWithActivityHistory_Is409_NotServerError()
    {
        // Amy (UserA2Id) has seeded chore completions ⇒ the RESTRICT FK blocks the delete. Parity: surface a
        // clean 409 (the island toasts it), NOT a 500 (review R-A: graceful FK handling).
        var del = await ClientA.DeleteAsync($"{MembersUrl}/{ChoresWebAppFactory.UserA2Id}");
        del.StatusCode.Should().Be(HttpStatusCode.Conflict);
        (await del.Content.ReadAsStringAsync()).Should().NotBeNullOrEmpty();
    }

    [Fact]
    public async Task Members_CrossHousehold_Mutation_Is404()
    {
        // Alice (household A) cannot toggle or delete bob (household B's UserBId) — M1.
        var toggle = await ClientA.PutAsJsonAsync($"{MembersUrl}/{ChoresWebAppFactory.UserBId}", new { isWhitelisted = false }, Json);
        toggle.StatusCode.Should().Be(HttpStatusCode.NotFound);

        var delete = await ClientA.DeleteAsync($"{MembersUrl}/{ChoresWebAppFactory.UserBId}");
        delete.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
