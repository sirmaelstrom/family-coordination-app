using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Models.SchemaOrg;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// End-to-end coverage of <c>POST /api/recipes/import/preview</c> through the real HTTP pipeline against real
/// Postgres. The SCRAPE is stubbed (per the existing scraper-test pattern — <c>RecipeImportServiceTests</c>
/// mocks <c>IRecipeScraperService</c>); everything downstream is real: <c>RecipeImportService</c>,
/// <c>IngredientParser</c>, <c>CategoryInferenceService</c>, and the endpoint's duplicate detection. The
/// <c>IUrlValidator</c> is also stubbed valid so no test does live DNS. Proves the preview endpoint returns
/// the parse (create-request shaped) and — the whole point — PERSISTS NOTHING (raw-row assertion). Failure
/// paths keep the import family's 200-envelope contract (success:false + non-empty errorMessage), and a
/// partial parse still returns partialData so the SPA can preview it.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class RecipeImportPreviewEndpointTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private readonly PreviewWebAppFactory _factory = new(postgres);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task InitializeAsync() => await _factory.EnsureSeededAsync();

    public async Task DisposeAsync() => await _factory.DisposeAsync();

    private HttpClient ClientA => _factory.CreateClientAs(ChoresWebAppFactory.UserAEmail);

    // ── Wire shapes (camelCase via JsonSerializerDefaults.Web) ──────────────────────
    private sealed record PreviewIngredient(
        string name, decimal? quantity, string? unit, string category, string? notes, string? groupName, int sortOrder);
    private sealed record PreviewRecipe(
        string name, string? description, string? instructions, string? sourceUrl, int? servings,
        int? prepTimeMinutes, int? cookTimeMinutes, string recipeType, string? imagePath,
        List<PreviewIngredient> ingredients);
    private sealed record PartialData(
        string? name, string? description, string? instructions, List<string>? ingredientStrings,
        string? imageUrl, int? prepTimeMinutes, int? cookTimeMinutes, int? servings);
    private sealed record PreviewResult(
        bool success, PreviewRecipe? recipe, string? errorMessage, string? errorType,
        int? existingRecipeId, string? existingRecipeName, PartialData? partialData);

    private static RecipeSchema FullSchema(string name = "Preview Pancakes") => new()
    {
        Name = name,
        Description = "Fluffy preview pancakes",
        RecipeIngredient = new[] { "2 cups flour", "1 egg" },
        RecipeInstructions = JsonSerializer.Deserialize<JsonElement>(
            "[{\"@type\":\"HowToStep\",\"text\":\"Mix\"},{\"@type\":\"HowToStep\",\"text\":\"Cook\"}]"),
        RecipeYield = JsonSerializer.Deserialize<JsonElement>("\"4 servings\""),
        PrepTime = "PT10M",
        CookTime = "PT20M",
        Image = JsonSerializer.Deserialize<JsonElement>("\"https://example.com/pancakes.jpg\"")
    };

    private async Task<int> CountRecipesBySourceUrlAsync(string url)
    {
        var dbFactory = _factory.Services.GetRequiredService<IDbContextFactory<ApplicationDbContext>>();
        await using var ctx = await dbFactory.CreateDbContextAsync();
        // IgnoreQueryFilters so even a soft-deleted accidental write would be caught.
        return await ctx.Recipes.IgnoreQueryFilters().CountAsync(r => r.SourceUrl == url);
    }

    [Fact]
    public async Task Preview_Success_ReturnsParse_AndPersistsNothing()
    {
        const string url = "https://preview.test/pancakes";
        _factory.Scraper.Next = FullSchema();

        var resp = await ClientA.PostAsJsonAsync("/api/recipes/import/preview", new { url, force = false }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<PreviewResult>(Json);
        result.Should().NotBeNull();
        result!.success.Should().BeTrue();
        result.errorMessage.Should().BeNull();
        result.recipe.Should().NotBeNull();

        var recipe = result.recipe!;
        recipe.name.Should().Be("Preview Pancakes");
        recipe.description.Should().Be("Fluffy preview pancakes");
        recipe.sourceUrl.Should().Be(url);
        recipe.imagePath.Should().Be("https://example.com/pancakes.jpg");
        recipe.servings.Should().Be(4);
        recipe.prepTimeMinutes.Should().Be(10);
        recipe.cookTimeMinutes.Should().Be(20);
        recipe.recipeType.Should().Be("main"); // default(RecipeType) — camelCase enum string on the wire
        recipe.instructions.Should().Contain("Mix").And.Contain("Cook");

        // The real IngredientParser ran ("2 cups flour" → 2 / "cups" / "flour").
        recipe.ingredients.Should().HaveCount(2);
        recipe.ingredients[0].quantity.Should().Be(2m);
        recipe.ingredients[0].unit.Should().Be("cups");
        recipe.ingredients[0].name.Should().Be("flour");
        recipe.ingredients[0].category.Should().NotBeNullOrWhiteSpace();
        recipe.ingredients.Select(i => i.sortOrder).Should().BeInAscendingOrder();

        // THE invariant: preview persisted nothing (raw-row check, query filters ignored).
        (await CountRecipesBySourceUrlAsync(url)).Should().Be(0);
    }

    [Fact]
    public async Task Preview_NoRecipeMarkup_ReturnsFailureEnvelope_AndPersistsNothing()
    {
        const string url = "https://preview.test/no-jsonld";
        _factory.Scraper.Next = null; // scraper found no JSON-LD Recipe

        var resp = await ClientA.PostAsJsonAsync("/api/recipes/import/preview", new { url, force = false }, Json);

        // Import-family convention: scrape failures are a 200 envelope with success:false + a message.
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<PreviewResult>(Json);
        result!.success.Should().BeFalse();
        result.recipe.Should().BeNull();
        result.errorType.Should().Be("ParsingFailed");
        result.errorMessage.Should().NotBeNullOrWhiteSpace();

        (await CountRecipesBySourceUrlAsync(url)).Should().Be(0);
    }

    [Fact]
    public async Task Preview_PartialParse_MissingIngredients_ReturnsPartialData_AndPersistsNothing()
    {
        const string url = "https://preview.test/partial";
        _factory.Scraper.Next = new RecipeSchema
        {
            Name = "Partial Soup",
            Description = "Only metadata, no ingredients",
            RecipeIngredient = null // → ValidationFailed with partial data
        };

        var resp = await ClientA.PostAsJsonAsync("/api/recipes/import/preview", new { url, force = false }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var result = await resp.Content.ReadFromJsonAsync<PreviewResult>(Json);
        result!.success.Should().BeFalse();
        result.recipe.Should().BeNull();
        result.errorType.Should().Be("ValidationFailed");
        result.errorMessage.Should().NotBeNullOrWhiteSpace();
        result.partialData.Should().NotBeNull();
        result.partialData!.name.Should().Be("Partial Soup");
        result.partialData.description.Should().Be("Only metadata, no ingredients");

        (await CountRecipesBySourceUrlAsync(url)).Should().Be(0);
    }

    [Fact]
    public async Task Preview_DuplicateUrl_ReturnsExisting_AndForceBypasses()
    {
        const string url = "https://preview.test/duplicate";

        // Seed a persisted recipe with that SourceUrl through the real create endpoint.
        var createResp = await ClientA.PostAsJsonAsync("/api/recipes", new
        {
            name = "Preview Dup Source",
            description = (string?)null,
            instructions = (string?)null,
            sourceUrl = url,
            servings = (int?)null,
            prepTimeMinutes = (int?)null,
            cookTimeMinutes = (int?)null,
            recipeType = "main",
            imagePath = (string?)null,
            ingredients = Array.Empty<object>()
        }, Json);
        createResp.StatusCode.Should().Be(HttpStatusCode.Created);

        var resp = await ClientA.PostAsJsonAsync("/api/recipes/import/preview", new { url, force = false }, Json);
        var result = await resp.Content.ReadFromJsonAsync<PreviewResult>(Json);
        result!.success.Should().BeFalse();
        result.existingRecipeId.Should().NotBeNull();
        result.existingRecipeName.Should().Be("Preview Dup Source");
        result.recipe.Should().BeNull();

        // force=true bypasses the duplicate check and previews — still without persisting a second row.
        _factory.Scraper.Next = FullSchema("Forced Preview");
        var forced = await ClientA.PostAsJsonAsync("/api/recipes/import/preview", new { url, force = true }, Json);
        var forcedResult = await forced.Content.ReadFromJsonAsync<PreviewResult>(Json);
        forcedResult!.success.Should().BeTrue();
        forcedResult.recipe!.name.Should().Be("Forced Preview");

        (await CountRecipesBySourceUrlAsync(url)).Should().Be(1); // only the seeded create
    }

    [Fact]
    public async Task Preview_EmptyUrl_Returns400_WithNonEmptyBody()
    {
        var resp = await ClientA.PostAsJsonAsync("/api/recipes/import/preview", new { url = "", force = false }, Json);

        resp.StatusCode.Should().Be(HttpStatusCode.BadRequest);
        var body = await resp.Content.ReadAsStringAsync();
        body.Should().NotBeNullOrWhiteSpace(); // /api 4xx must carry a body (re-execute quirk)
        body.Should().Contain("URL");
    }
}

/// <summary>
/// <see cref="ChoresWebAppFactory"/> variant for the import-preview tests: replaces
/// <see cref="IRecipeScraperService"/> with a configurable stub (no real network) and
/// <see cref="IUrlValidator"/> with an always-valid stub (no live DNS). Everything else — the real
/// <c>RecipeImportService</c> pipeline, endpoints, auth, Postgres — is the production wiring.
/// </summary>
public sealed class PreviewWebAppFactory(PostgresContainerFixture postgres) : ChoresWebAppFactory(postgres)
{
    public StubRecipeScraper Scraper { get; } = new();

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        base.ConfigureWebHost(builder);
        builder.ConfigureServices(services =>
        {
            services.AddSingleton<IRecipeScraperService>(Scraper);
            services.AddSingleton<IUrlValidator>(new AlwaysValidUrlValidator());
        });
    }

    /// <summary>Returns whatever a test placed in <see cref="Next"/> (null ⇒ "no JSON-LD found").</summary>
    public sealed class StubRecipeScraper : IRecipeScraperService
    {
        public RecipeSchema? Next { get; set; }

        public Task<RecipeSchema?> ScrapeRecipeAsync(string url, CancellationToken cancellationToken = default)
            => Task.FromResult(Next);

        public Task<string> FetchHtmlAsync(string url, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not exercised by the import pipeline under test.");

        public Task<RecipeSchema?> ExtractJsonLdAsync(string html, CancellationToken cancellationToken = default)
            => throw new NotSupportedException("Not exercised by the import pipeline under test.");
    }

    private sealed class AlwaysValidUrlValidator : IUrlValidator
    {
        public bool IsUrlSafe(string url) => true;
        public (bool IsValid, string? ErrorMessage) ValidateUrl(string url) => (true, null);
    }
}
