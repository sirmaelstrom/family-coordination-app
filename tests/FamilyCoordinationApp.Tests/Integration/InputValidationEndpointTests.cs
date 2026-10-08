using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Central request validation on <c>/api</c> (quest ec7a7331). Before it, a request string longer than its column
/// reached Postgres, failed with 22001 (string data right truncation) and the caller got a 500. Each case below
/// sends one over-long value through the real pipeline and requires a 400 whose JSON body carries a non-empty
/// <c>message</c> — the field the SPA's <c>messageFrom</c> shows; without it the user sees the raw body text.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class InputValidationEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly ChoresWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    private static string Over(int limit) => new('x', limit + 1);

    private sealed record BoardChore(int id, uint version);
    private sealed record Board(List<BoardChore> chores);
    private sealed record IdDto(int id);
    private sealed record CategoryDto(int categoryId);

    /// <summary>The 400 contract: status, a JSON body, and a non-empty human-readable <c>message</c>.</summary>
    private static async Task<string> AssertValidation400(HttpResponseMessage resp, string expectedInMessage)
    {
        var body = await resp.Content.ReadAsStringAsync();
        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest, "an over-long value is the caller's error, not a 500. Body: {0}", body);

        using var doc = JsonDocument.Parse(body);
        doc.RootElement.TryGetProperty("message", out var message).Should().BeTrue("the SPA shows `message`; body was {0}", body);
        message.ValueKind.Should().Be(JsonValueKind.String);
        var text = message.GetString();
        text.Should().NotBeNullOrWhiteSpace();
        text.Should().Contain(expectedInMessage);
        return text!;
    }

    // ── Chores ─────────────────────────────────────────────────────────────────────────────────

    private static object ChoreBody(string name, string? description = null) => new
    {
        name,
        description,
        recurrenceMode = "flexible",
        intervalDays = 7,
        effortTier = "standard",
    };

    [Fact]
    public async Task CreateChore_NameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/chores/", ChoreBody(Over(200)), Json);
        var message = await AssertValidation400(resp, "200");
        message.Should().Be("Chore name must be 200 characters or fewer.");
    }

    [Fact]
    public async Task CreateChore_DescriptionTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/chores/", ChoreBody("Valid chore", Over(2000)), Json);
        await AssertValidation400(resp, "2000");
    }

    [Fact]
    public async Task UpdateChore_NameTooLong_Returns400WithMessage()
    {
        var board = await ClientA.GetFromJsonAsync<Board>("/api/chores/board", Json);
        var chore = board!.chores.Single(c => c.id == ChoresWebAppFactory.PileChoreAId);

        var resp = await ClientA.PutAsJsonAsync($"/api/chores/{chore.id}", new
        {
            name = Over(200),
            recurrenceMode = "flexible",
            intervalDays = 7,
            effortTier = "standard",
            version = chore.version,
        }, Json);

        await AssertValidation400(resp, "200");
    }

    // ── Rooms ──────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateRoom_NameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/rooms/", new { name = Over(100) }, Json);
        var message = await AssertValidation400(resp, "100");
        message.Should().Be("Room name must be 100 characters or fewer.");
    }

    [Fact]
    public async Task UpdateRoom_NameTooLong_Returns400WithMessage()
    {
        var created = await ClientA.PostAsJsonAsync("/api/rooms/", new { name = "Validation room" }, Json);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var room = (await created.Content.ReadFromJsonAsync<IdDto>(Json))!;

        var resp = await ClientA.PutAsJsonAsync($"/api/rooms/{room.id}", new { name = Over(100) }, Json);
        await AssertValidation400(resp, "100");
    }

    // ── Shopping lists ─────────────────────────────────────────────────────────────────────────

    private async Task<int> CreateListAsync()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/shopping-lists/", new { name = "Validation list" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
        return (await resp.Content.ReadFromJsonAsync<IdDto>(Json))!.id;
    }

    [Fact]
    public async Task CreateList_NameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/shopping-lists/", new { name = Over(200) }, Json);
        await AssertValidation400(resp, "200");
    }

    [Fact]
    public async Task RenameList_NameTooLong_Returns400WithMessage()
    {
        var listId = await CreateListAsync();
        var resp = await ClientA.PostAsJsonAsync($"/api/shopping-lists/{listId}/actions/rename", new { name = Over(200) }, Json);
        await AssertValidation400(resp, "200");
    }

    [Fact]
    public async Task AddItem_NameTooLong_Returns400WithMessage()
    {
        var listId = await CreateListAsync();
        var resp = await ClientA.PostAsJsonAsync($"/api/shopping-lists/{listId}/items", new { name = Over(200) }, Json);
        await AssertValidation400(resp, "200");
    }

    [Fact]
    public async Task PatchItem_NameTooLong_Returns400WithMessage()
    {
        var listId = await CreateListAsync();
        var added = await ClientA.PostAsJsonAsync($"/api/shopping-lists/{listId}/items", new { name = "Milk" }, Json);
        added.StatusCode.Should().Be(HttpStatusCode.Created);
        var item = (await added.Content.ReadFromJsonAsync<IdDto>(Json))!;

        var resp = await ClientA.PatchAsJsonAsync($"/api/shopping-lists/{listId}/items/{item.id}", new { name = Over(200) }, Json);
        await AssertValidation400(resp, "200");
    }

    // ── Recipes + meal plan ────────────────────────────────────────────────────────────────────

    private static object RecipeBody(string name, string ingredientName = "Flour") => new
    {
        name,
        recipeType = "main",
        ingredients = new[] { new { name = ingredientName, category = "Pantry", sortOrder = 0 } },
    };

    [Fact]
    public async Task CreateRecipe_NameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/recipes/", RecipeBody(Over(200)), Json);
        var message = await AssertValidation400(resp, "200");
        message.Should().Be("Recipe name must be 200 characters or fewer.");
    }

    [Fact]
    public async Task CreateRecipe_IngredientNameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/recipes/", RecipeBody("Valid recipe", Over(200)), Json);
        await AssertValidation400(resp, "200");
    }

    [Fact]
    public async Task QuickCreateRecipe_NameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/meal-plan/recipes", new { name = Over(200), recipeType = "main" }, Json);
        await AssertValidation400(resp, "200");
    }

    [Fact]
    public async Task AddMealEntry_CustomMealNameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/meal-plan/entries",
            new { date = "2026-06-08", mealType = "dinner", customMealName = Over(200) }, Json);
        await AssertValidation400(resp, "200");
    }

    // ── Categories ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task CreateCategory_NameTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/settings/categories/",
            new { name = Over(50), iconEmoji = (string?)null, color = "#123456" }, Json);
        var message = await AssertValidation400(resp, "50");
        message.Should().Be("Category name must be 50 characters or fewer.");
    }

    [Fact]
    public async Task CreateCategory_ColorTooLong_Returns400WithMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/settings/categories/",
            new { name = "Validation color", iconEmoji = (string?)null, color = "#1234567" }, Json);
        await AssertValidation400(resp, "7");
    }

    [Fact]
    public async Task UpdateCategory_NameTooLong_Returns400WithMessage()
    {
        var created = await ClientA.PostAsJsonAsync("/api/settings/categories/",
            new { name = "Validation category", iconEmoji = (string?)null, color = "#123456" }, Json);
        created.StatusCode.Should().Be(HttpStatusCode.Created);
        var category = (await created.Content.ReadFromJsonAsync<CategoryDto>(Json))!;

        var resp = await ClientA.PutAsJsonAsync($"/api/settings/categories/{category.categoryId}",
            new { name = Over(50), iconEmoji = (string?)null, color = "#123456" }, Json);
        await AssertValidation400(resp, "50");
    }

    // ── Required (the checks the attributes replaced keep their message) ───────────────────────

    [Theory]
    [InlineData("/api/rooms/", "Room name is required.")]
    [InlineData("/api/shopping-lists/", "List name is required.")]
    [InlineData("/api/meal-plan/recipes", "Recipe name is required.")]
    public async Task BlankName_Returns400WithRequiredMessage(string url, string expected)
    {
        var resp = await ClientA.PostAsJsonAsync(url, new { name = "   ", recipeType = "main" }, Json);
        var message = await AssertValidation400(resp, "required");
        message.Should().Be(expected);
    }

    [Fact]
    public async Task BlankCategoryName_KeepsItsMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/settings/categories/",
            new { name = "", iconEmoji = (string?)null, color = "#123456" }, Json);
        var message = await AssertValidation400(resp, "required");
        message.Should().Be("Category name is required.", "the hand-written check this replaced said exactly this");
    }

    [Fact]
    public async Task MissingEmail_KeepsItsMessage()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/settings/members/", new { }, Json);
        var message = await AssertValidation400(resp, "required");
        message.Should().Be("Email is required.", "the hand-written check this replaced said exactly this");
    }

    /// <summary>
    /// AddMember stores the email's part before the <c>@</c> as the new user's display name (200-char column), while
    /// the email itself may be 256. Both a long local part and a long string with no <c>@</c> (the whole string becomes
    /// the name) must be refused with a 400 before the write, not reach Postgres as a 500 (amendment 1, item 1).
    /// </summary>
    [Theory]
    [InlineData("long-local-part")]
    [InlineData("no-at-sign")]
    public async Task AddMember_DerivedDisplayNameTooLong_Returns400WithMessage(string shape)
    {
        var email = shape == "no-at-sign"
            ? new string('n', 201)
            : new string('a', 201) + "@x.test";

        var resp = await ClientA.PostAsJsonAsync("/api/settings/members/", new { email }, Json);

        var message = await AssertValidation400(resp, "200");
        message.Should().Be("The name taken from this email (the part before the @) must be 200 characters or fewer.");
    }

    [Fact]
    public async Task AddMember_DerivedDisplayNameAtTheLimit_Succeeds()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/settings/members/",
            new { email = new string('m', 200) + "@x.test" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    // ── The 400 body shape (pinned) ────────────────────────────────────────────────────────────

    /// <summary>
    /// Pins the validation failure's wire shape: an <c>application/problem+json</c> body with the standard
    /// <c>status</c> + <c>errors</c>, plus the <c>message</c> the SPA reads, equal to the first error's text.
    /// </summary>
    [Fact]
    public async Task ValidationFailure_BodyShape_IsProblemJsonWithMessageAndErrors()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/rooms/", new { name = Over(100), icon = Over(30) }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        resp.Content.Headers.ContentType!.MediaType.Should().Be("application/problem+json");

        using var doc = JsonDocument.Parse(await resp.Content.ReadAsStringAsync());
        var root = doc.RootElement;
        root.GetProperty("status").GetInt32().Should().Be(400);
        root.GetProperty("message").GetString().Should().Be("Room name must be 100 characters or fewer.");

        var errors = root.GetProperty("errors");
        errors.GetProperty("Name")[0].GetString().Should().Be("Room name must be 100 characters or fewer.");
        errors.GetProperty("Icon")[0].GetString().Should().Be("Room icon must be 30 characters or fewer.");
    }

    /// <summary>
    /// The default problem-details writer refuses a request whose <c>Accept</c> excludes JSON, and the validation
    /// filter then falls back to a body with no <c>message</c>. The /api writer accepts every /api request.
    /// </summary>
    [Fact]
    public async Task ValidationFailure_WithNonJsonAccept_StillCarriesMessage()
    {
        var request = new HttpRequestMessage(HttpMethod.Post, "/api/rooms/")
        {
            Content = JsonContent.Create(new { name = Over(100) }, options: Json),
        };
        request.Headers.Accept.ParseAdd("text/html");

        var resp = await ClientA.SendAsync(request);

        await AssertValidation400(resp, "100");
    }

    // ── Controls: the limit is the column's, not one character less ────────────────────────────

    [Fact]
    public async Task CreateRoom_NameAtTheLimit_Succeeds()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/rooms/", new { name = new string('r', 100) }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task CreateCategory_ColorAtTheLimit_Succeeds()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/settings/categories/",
            new { name = "Validation color ok", iconEmoji = (string?)null, color = "#ABCDEF" }, Json);
        resp.StatusCode.Should().Be(HttpStatusCode.Created);
    }
}
