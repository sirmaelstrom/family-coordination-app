using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Endpoints.Mcp;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// The <c>/mcp</c> endpoint end to end (quest 8bfa42cf, design §7 PR 3) with the official MCP C# client over real
/// Streamable HTTP against the real host and Postgres: initialize/list/call, the read/write split (a read token is
/// refused writes whatever its arguments), revocation between calls of one connected client, two households in
/// parallel, argument checking and the tool-error shape.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class McpEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly ChoresWebAppFactory _factory = new(postgres) { McpEnabled = true };

    private const string Monday = "2026-11-09";

    private sealed record Minted(int id, string token);

    public async Task InitializeAsync()
    {
        await _factory.EnsureSeededAsync();
        AgentToolGate.ResetLimits();
    }

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private async Task<Minted> MintAsync(string email, string access)
    {
        var response = await _factory.CreateClientAs(email)
            .PostAsJsonAsync("/api/agent-tokens", new { label = $"{access} test", access, expiresInDays = 30 });
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<Minted>(Json))!;
    }

    private async Task<McpClient> ConnectAsync(string token)
    {
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
        var transport = new HttpClientTransport(
            new HttpClientTransportOptions { Endpoint = new Uri(_factory.Server.BaseAddress, "/mcp"), Name = "mcp-endpoint-tests" },
            http,
            ownsHttpClient: true);
        return await McpClient.CreateAsync(transport);
    }

    private static async Task<JsonElement> CallOk(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        result.IsError.Should().NotBe(true, Text(result));
        return result.StructuredContent!.Value;
    }

    private static async Task<(string Code, string Text)> CallError(McpClient client, string tool, Dictionary<string, object?> args)
    {
        var result = await client.CallToolAsync(tool, args);
        result.IsError.Should().BeTrue(Text(result));
        var code = result.StructuredContent!.Value.GetProperty("error").GetProperty("code").GetString()!;
        return (code, Text(result));
    }

    private static string Text(CallToolResult result) =>
        string.Join("\n", result.Content.OfType<TextContentBlock>().Select(t => t.Text));

    private static Dictionary<string, object?> Chili(string requestId) => new()
    {
        ["name"] = "MCP acceptance chili",
        ["recipeType"] = "main",
        ["servings"] = 4,
        ["ingredients"] = new object[]
        {
            new { name = "black beans", quantity = 2, unit = "cup", category = "Pantry" },
            new { name = "onion", quantity = 1 },
        },
        ["requestId"] = requestId,
    };

    private ApplicationDbContext Raw() => new PostgresDbContextFactory(_factory.ConnectionString).CreateDbContext();

    [Fact]
    public async Task A_write_token_adds_a_recipe_plans_a_week_and_builds_its_list()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);

        (await client.ListToolsAsync()).Select(t => t.Name).Should().BeEquivalentTo(AgentToolGate.Tools.Keys);

        var recipe = await CallOk(client, "create_recipe", Chili("e2e-recipe-01"));
        var recipeId = recipe.GetProperty("result").GetProperty("recipeId").GetInt32();

        var monday = DateOnly.Parse(Monday);
        for (var day = 0; day < 7; day++)
        {
            var added = await CallOk(client, "add_meal", new()
            {
                ["date"] = monday.AddDays(day).ToString("yyyy-MM-dd"),
                ["mealType"] = "dinner",
                ["recipeId"] = recipeId,
                ["servings"] = 8,
                ["requestId"] = $"e2e-meal-{day:00}",
            });
            var meal = added.GetProperty("result").GetProperty("meal");
            meal.GetProperty("servings").GetInt32().Should().Be(8);
        }

        var week = await CallOk(client, "get_meal_plan", new() { ["weekStart"] = Monday });
        week.GetProperty("meals").GetArrayLength().Should().Be(7);

        var list = await CallOk(client, "generate_shopping_list", new() { ["weekStart"] = Monday, ["requestId"] = "e2e-list-01" });
        var beans = list.GetProperty("result").GetProperty("items").EnumerateArray()
            .Single(i => i.GetProperty("name").GetString()!.Contains("black beans", StringComparison.OrdinalIgnoreCase));
        beans.GetProperty("quantity").GetDecimal().Should().Be(28m);
        list.GetProperty("result").GetProperty("pantryAdjusted").GetBoolean().Should().BeFalse();

        var listId = list.GetProperty("result").GetProperty("listId").GetInt32();
        var read = await CallOk(client, "get_shopping_lists", new() { ["listId"] = listId });
        read.GetProperty("detail").GetProperty("items").GetArrayLength().Should().BeGreaterThan(0);

        await using var db = Raw();
        (await db.AgentOperations.CountAsync(o => o.Tool == "add_meal")).Should().Be(7);
        (await db.AgentOperations.CountAsync(o => o.Tool == "set_meal_servings")).Should().Be(0);
    }

    [Fact]
    public async Task Add_meal_schema_exposes_optional_nullable_integer_servings()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);
        var tool = (await client.ListToolsAsync()).Single(t => t.Name == "add_meal");
        var schema = tool.JsonSchema;
        var servings = schema.GetProperty("properties").GetProperty("servings");
        servings.GetProperty("type").EnumerateArray().Select(t => t.GetString())
            .Should().BeEquivalentTo("integer", "null");
        schema.GetProperty("required").EnumerateArray().Select(t => t.GetString()).Should().NotContain("servings");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Add_meal_servings_are_insert_only_and_dry_runs_persist_nothing(bool useRecipe)
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);
        int? recipeId = useRecipe
            ? (await CallOk(client, "create_recipe", Chili("servings-recipe-01"))).GetProperty("result").GetProperty("recipeId").GetInt32()
            : null;

        Dictionary<string, object?> Args(string requestId, int? servings = 8, bool dryRun = false) => new()
        {
            ["date"] = Monday, ["mealType"] = "dinner", ["recipeId"] = recipeId,
            ["customMealName"] = useRecipe ? null : "Leftovers", ["notes"] = "Birthday",
            ["servings"] = servings, ["requestId"] = requestId, ["dryRun"] = dryRun,
        };

        var dry = await CallOk(client, "add_meal", Args("servings-insert-01", dryRun: true));
        dry.GetProperty("dryRun").GetBoolean().Should().BeTrue();
        dry.GetProperty("result").GetProperty("meal").GetProperty("servings").GetInt32().Should().Be(8);
        await using (var db = Raw())
        {
            (await db.MealPlanEntries.CountAsync()).Should().Be(0);
            (await db.MealPlans.CountAsync()).Should().Be(0);
            (await db.AgentOperations.CountAsync(o => o.Tool == "add_meal")).Should().Be(0);
        }

        var inserted = await CallOk(client, "add_meal", Args("servings-insert-01"));
        inserted.GetProperty("replayed").GetBoolean().Should().BeFalse();
        inserted.GetProperty("result").GetProperty("outcome").GetString().Should().Be("added");
        var meal = inserted.GetProperty("result").GetProperty("meal");
        meal.GetProperty("servings").GetInt32().Should().Be(8);

        var replay = await CallOk(client, "add_meal", Args("servings-insert-01"));
        replay.GetProperty("replayed").GetBoolean().Should().BeTrue();
        var same = await CallOk(client, "add_meal", Args("servings-same-01"));
        same.GetProperty("result").GetProperty("outcome").GetString().Should().Be("alreadyPlanned");
        same.GetProperty("result").GetProperty("meal").ToString().Should().Be(meal.ToString());

        var omitted = Args("servings-omitted-01");
        omitted.Remove("servings");
        var unchanged = await CallOk(client, "add_meal", omitted);
        unchanged.GetProperty("result").GetProperty("meal").ToString().Should().Be(meal.ToString());
        var cleared = await CallOk(client, "add_meal", Args("servings-null-01", servings: null));
        cleared.GetProperty("result").GetProperty("meal").ToString().Should().Be(meal.ToString());

        foreach (var dryRun in new[] { false, true })
        {
            var conflict = await CallError(client, "add_meal", Args($"servings-conflict-{dryRun}", 6, dryRun));
            conflict.Code.Should().Be("conflict");
            conflict.Text.Should().Contain("different servings").And.Contain("Nothing was changed");
        }
        (await CallError(client, "add_meal", Args("servings-insert-01", 6))).Code.Should().Be("conflict", "servings participates in request-id hashing");

        var dryDuplicate = await CallOk(client, "add_meal", Args("servings-dry-same", dryRun: true));
        dryDuplicate.GetProperty("result").GetProperty("outcome").GetString().Should().Be("alreadyPlanned");
        await using var check = Raw();
        var row = await check.MealPlanEntries.SingleAsync();
        row.Servings.Should().Be(8);
        row.Version.Should().Be(meal.GetProperty("version").GetUInt32());
        row.Notes.Should().Be("Birthday");
        row.UpdatedAt.Should().BeNull();
        (await check.AgentOperations.CountAsync(o => o.Tool == "add_meal")).Should().Be(4);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1001)]
    public async Task Add_meal_rejects_invalid_servings_without_writes(int servings)
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);
        var error = await CallError(client, "add_meal", new()
        {
            ["date"] = Monday, ["mealType"] = "dinner", ["customMealName"] = "Leftovers",
            ["servings"] = servings, ["requestId"] = "servings-invalid-01",
        });
        error.Code.Should().Be("invalid_argument");
        error.Text.Should().Contain("Servings");
        await using var db = Raw();
        (await db.MealPlanEntries.CountAsync()).Should().Be(0);
        (await db.MealPlans.CountAsync()).Should().Be(0);
        (await db.AgentOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_read_token_sees_only_read_tools_and_is_refused_every_write()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "read");
        await using var client = await ConnectAsync(token.token);

        (await client.ListToolsAsync()).Select(t => t.Name).Should().BeEquivalentTo(
            AgentToolGate.Tools.Where(t => t.Value == AgentToolGate.Access.Read).Select(t => t.Key));

        await using var before = Raw();
        var recipes = await before.Recipes.CountAsync();

        foreach (var (tool, args) in new (string, Dictionary<string, object?>)[]
                 {
                     ("create_recipe", Chili("read-forged-01")),
                     ("add_meal", new() { ["date"] = Monday, ["mealType"] = "dinner", ["customMealName"] = "x", ["requestId"] = "read-forged-02" }),
                     ("generate_shopping_list", new() { ["weekStart"] = Monday, ["requestId"] = "read-forged-03", ["dryRun"] = true }),
                 })
        {
            var (code, text) = await CallError(client, tool, args);
            code.Should().Be("forbidden");
            text.Should().Contain("read-only");
        }

        await using var after = Raw();
        (await after.Recipes.CountAsync()).Should().Be(recipes);
        (await after.AgentOperations.CountAsync()).Should().Be(0);
    }

    [Fact]
    public async Task A_token_revoked_between_calls_is_refused_on_its_next_call()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);
        await CallOk(client, "search_recipes", new());

        (await _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail).DeleteAsync($"/api/agent-tokens/{token.id}"))
            .StatusCode.Should().Be(HttpStatusCode.NoContent);

        var next = async () => await client.CallToolAsync("search_recipes", new Dictionary<string, object?>());
        (await next.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Missing_or_wrong_bearer_cannot_connect()
    {
        var missing = async () => { await using var _ = await ConnectAsync(""); };
        await missing.Should().ThrowAsync<HttpRequestException>();

        var wrong = async () => { await using var _ = await ConnectAsync("fca_pat_AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA"); };
        (await wrong.Should().ThrowAsync<HttpRequestException>()).Which.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Two_households_in_parallel_see_only_their_own_data()
    {
        var a = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        var b = await MintAsync(ChoresWebAppFactory.UserBEmail, "readWrite");
        await using var clientA = await ConnectAsync(a.token);
        await using var clientB = await ConnectAsync(b.token);

        async Task<int> CreateMany(McpClient client, string house) =>
            (await Task.WhenAll(Enumerable.Range(0, 5).Select(i =>
            {
                var args = Chili($"par-{house}-{i:00}");
                args["name"] = $"{house} recipe {i}";
                return CallOk(client, "create_recipe", args);
            }))).Length;

        await Task.WhenAll(CreateMany(clientA, "A"), CreateMany(clientB, "B"));

        var namesA = (await CallOk(clientA, "search_recipes", new() { ["limit"] = 50 })).GetProperty("recipes").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();
        var namesB = (await CallOk(clientB, "search_recipes", new() { ["limit"] = 50 })).GetProperty("recipes").EnumerateArray()
            .Select(r => r.GetProperty("name").GetString()).ToList();
        namesA.Should().HaveCount(5).And.OnlyContain(n => n!.StartsWith("A recipe"));
        namesB.Should().HaveCount(5).And.OnlyContain(n => n!.StartsWith("B recipe"));

        await using var db = Raw();
        (await db.Recipes.CountAsync(r => r.HouseholdId == ChoresWebAppFactory.HouseholdAId && r.Name.StartsWith("B "))).Should().Be(0);
        (await db.Recipes.CountAsync(r => r.HouseholdId == ChoresWebAppFactory.HouseholdBId && r.Name.StartsWith("A "))).Should().Be(0);
    }

    [Fact]
    public async Task Bad_arguments_are_tool_errors_that_say_what_to_fix()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);

        (await CallError(client, "get_meal_plan", new() { ["weekStart"] = "2026-02-30" })).Code.Should().Be("invalid_argument");
        (await CallError(client, "add_meal", new() { ["date"] = Monday, ["mealType"] = "brunch", ["customMealName"] = "x", ["requestId"] = "bad-enum-001" }))
            .Text.Should().Contain("breakfast, lunch, dinner, snack");
        (await CallError(client, "search_recipes", new() { ["householdId"] = 2 })).Text.Should().Contain("unknown argument");
        (await CallError(client, "get_recipe", new() { ["recipeId"] = 4242 })).Code.Should().Be("not_found");
        (await CallError(client, "generate_shopping_list", new() { ["weekStart"] = "2026-11-10", ["requestId"] = "not-monday-1" }))
            .Text.Should().Contain("must be a Monday");
        (await CallError(client, "create_recipe", Chili("short"))).Text.Should().Contain("requestId");

        var import = await CallError(client, "preview_recipe_import", new() { ["url"] = "http://127.0.0.1/recipe" });
        import.Code.Should().Be("invalid_argument");
        import.Text.Should().Contain("internal networks").And.Contain("No recipe was saved");
    }

    [Fact]
    public async Task A_retried_write_replays_and_says_so()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);

        var first = await CallOk(client, "create_recipe", Chili("replay-key-01"));
        var second = await CallOk(client, "create_recipe", Chili("replay-key-01"));

        first.GetProperty("replayed").GetBoolean().Should().BeFalse();
        second.GetProperty("replayed").GetBoolean().Should().BeTrue();
        second.GetProperty("result").GetProperty("recipeId").GetInt32()
            .Should().Be(first.GetProperty("result").GetProperty("recipeId").GetInt32());
    }

    [Fact]
    public async Task Writes_are_rate_limited_per_token()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "readWrite");
        await using var client = await ConnectAsync(token.token);

        // Dry runs persist nothing and do not spend the write budget.
        for (var i = 0; i < AgentToolGate.WritesPerMinute + 5; i++)
        {
            (await CallError(client, "generate_shopping_list", new() { ["weekStart"] = Monday, ["requestId"] = $"dry-{i:000}", ["dryRun"] = true }))
                .Code.Should().Be("invalid_argument");
        }

        for (var i = 0; i < AgentToolGate.WritesPerMinute; i++)
        {
            // Refused by the command (no meals that week), but each one counts as a write attempt.
            (await CallError(client, "generate_shopping_list", new() { ["weekStart"] = Monday, ["requestId"] = $"rate-{i:000}" }))
                .Code.Should().Be("invalid_argument");
        }

        var (code, text) = await CallError(client, "generate_shopping_list", new() { ["weekStart"] = Monday, ["requestId"] = "rate-over-1" });
        code.Should().Be("rate_limited");
        text.Should().Contain("nothing was run");
        (await CallOk(client, "search_recipes", new())).GetProperty("total").GetInt32().Should().BeGreaterThanOrEqualTo(0, "reads have their own, larger budget");
    }

    [Fact]
    public async Task A_browser_origin_is_refused()
    {
        var token = await MintAsync(ChoresWebAppFactory.UserAEmail, "read");
        var http = _factory.CreateClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.token);
        var request = new HttpRequestMessage(HttpMethod.Post, "/mcp")
        {
            Content = JsonContent.Create(new { jsonrpc = "2.0", id = 1, method = "tools/list" }),
        };
        request.Headers.Add("Origin", "https://evil.example");
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.Accept.ParseAdd("text/event-stream");

        (await http.SendAsync(request)).StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }
}
