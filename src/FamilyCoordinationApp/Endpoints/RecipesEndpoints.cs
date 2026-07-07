using System.Security.Claims;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Endpoints;

/// <summary>
/// Minimal-API surface for the recipes island (strangler — mirrors <see cref="MealPlanEndpoints"/>): a
/// <c>/api/recipes</c> group behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every handler resolving
/// the HouseholdId/UserId from the authenticated caller (M1, never client-supplied) via
/// <see cref="UserContextResolver"/>. Writes delegate to <see cref="IRecipeService"/> / <see cref="IImageService"/>
/// / <see cref="IDraftService"/> / <see cref="IRecipeImportService"/>; recipe→DTO shaping goes through the ONE
/// <see cref="IRecipeProjectionService"/> (no card/detail drift, M9). Kept SEPARATE from the meal-plan picker's
/// <c>/api/meal-plan/recipes/*</c> (spec D11).
///
/// <para>Full-form edits are optimistic-concurrency guarded: GET carries the xmin token (<c>version</c>),
/// PUT echoes it back, and a stale token → 409 with a non-empty body (<see cref="RecipeConflictException"/>).
/// A null token skips the check (legacy last-write-wins). Not-found responses carry a
/// NON-EMPTY body so the app-global <c>UseStatusCodePagesWithReExecute</c> leaves them as clean 404s (an empty
/// 404 surfaces as an empty 400/405). <c>Recipe</c> has an EF global query filter on <c>IsDeleted</c>
/// (<c>RecipeConfiguration</c>), so soft-deleted recipes are auto-excluded — no explicit filter here.</para>
/// </summary>
public static class RecipesEndpoints
{
    public static IEndpointRouteBuilder MapRecipesEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/recipes")
            .RequireAuthorization()
            // Same rationale as the other island groups: same-origin credentialed fetch + JSON/multipart bodies,
            // SameSite auth cookie — the antiforgery token the SPA can't readily supply is not the CSRF control.
            .DisableAntiforgery();

        // ⚠ Register all LITERAL-segment routes BEFORE the parameterized /{recipeId:int} routes. A non-GET
        // literal (PUT/DELETE /draft) that shares the root level with PUT/DELETE /{recipeId:int} was being
        // shadowed by the parameterized route at match time (→ 404 → re-executed to 405); registering literals
        // first makes the matcher prefer them deterministically. (GET literals happened to resolve regardless.)

        // List (root)
        group.MapGet("/", ListRecipes);
        group.MapPost("/", CreateRecipe);

        // Ingredient entry helpers (literal)
        group.MapGet("/ingredient-suggestions", IngredientSuggestions);
        group.MapPost("/parse-ingredient", ParseIngredient);
        group.MapPost("/parse-ingredients", ParseIngredients);
        group.MapGet("/categories", GetCategories);

        // Images (literal)
        group.MapPost("/images", UploadImage);
        group.MapGet("/images", ListImages);

        // Import (literal)
        group.MapPost("/import", ImportRecipe);

        // Connected households (literal prefix)
        group.MapGet("/connections", GetConnections);
        group.MapGet("/connected/{chId:int}", ListConnectedRecipes);
        group.MapGet("/connected/{chId:int}/{recipeId:int}", GetConnectedRecipe);
        group.MapPost("/connected/{chId:int}/{recipeId:int}/copy", CopyConnectedRecipe);

        // Drafts (autosave). recipeId omitted ⇒ a "new recipe" draft (the endpoints use a 0 sentinel — the
        // RecipeDraft composite PK includes RecipeId, which can't be null; there's no FK on it, so 0 is safe).
        group.MapGet("/draft", GetDraft);
        group.MapPut("/draft", SaveDraft);
        group.MapDelete("/draft", DeleteDraft);

        // Single-recipe by id (parameterized — registered last)
        group.MapGet("/{recipeId:int}", GetRecipe);
        group.MapPut("/{recipeId:int}", UpdateRecipe);
        group.MapDelete("/{recipeId:int}", DeleteRecipe);
        group.MapPost("/{recipeId:int}/favorite", ToggleFavorite);

        return app;
    }

    // ─── List + detail ───────────────────────────────────────────────────────────

    private static async Task<IResult> ListRecipes(
        [FromQuery] string? q,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IRecipeProjectionService projection,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var recipes = await recipeService.GetRecipesAsync(user.HouseholdId, q, ct);
        var favorites = await recipeService.GetFavoriteRecipeIdsAsync(user.UserId, user.HouseholdId, ct);

        var items = recipes.Select(projection.ToListItem).ToList();
        return Results.Ok(new RecipeListDto(items, favorites.ToList()));
    }

    private static async Task<IResult> GetRecipe(
        int recipeId,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IRecipeProjectionService projection,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var recipe = await recipeService.GetRecipeAsync(user.HouseholdId, recipeId, ct);
        if (recipe is null) return Results.NotFound(new { message = "Recipe not found." });

        return Results.Ok(projection.ToFull(recipe));
    }

    private static async Task<IResult> CreateRecipe(
        RecipeWriteRequest req,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IRecipeProjectionService projection,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Recipe name is required." });
        }

        var recipe = MapToRecipe(req, user.HouseholdId, recipeId: 0);
        recipe.CreatedByUserId = user.UserId;
        recipe.CreatedAt = DateTime.UtcNow;

        var created = await recipeService.CreateRecipeAsync(recipe, ct);

        // Re-fetch so the response projection has the CreatedBy nav (CreateRecipeAsync doesn't load it).
        var full = await recipeService.GetRecipeAsync(user.HouseholdId, created.RecipeId, ct);
        return Results.Created($"/api/recipes/{created.RecipeId}", projection.ToFull(full!));
    }

    private static async Task<IResult> UpdateRecipe(
        int recipeId,
        RecipeWriteRequest req,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IRecipeProjectionService projection,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Recipe name is required." });
        }

        var recipe = MapToRecipe(req, user.HouseholdId, recipeId);
        recipe.UpdatedByUserId = user.UserId;

        try
        {
            // req.Version is the xmin token echoed from GET (null ⇒ legacy client ⇒ last-write-wins).
            await recipeService.UpdateRecipeAsync(recipe, req.Version, ct);
        }
        catch (InvalidOperationException)
        {
            // Household-scoped load missed (missing / cross-household id) → clean 404 (non-empty body, M1).
            return Results.NotFound(new { message = "Recipe not found." });
        }
        catch (RecipeConflictException)
        {
            // Stale xmin token — someone else saved since this client loaded the recipe. Non-empty body (M1).
            return Results.Conflict(new { message = "This recipe was changed by someone else. Reload to see the latest." });
        }

        // Project from a FRESH load, NOT the UpdateRecipeAsync return — its Ingredients nav is stale after the
        // RemoveRange/Add (spec §11.11 / council).
        var full = await recipeService.GetRecipeAsync(user.HouseholdId, recipeId, ct);
        return Results.Ok(projection.ToFull(full!));
    }

    private static async Task<IResult> DeleteRecipe(
        int recipeId,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            await recipeService.DeleteRecipeAsync(user.HouseholdId, recipeId, ct);
            return Results.NoContent();
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new { message = "Recipe not found." });
        }
    }

    private static async Task<IResult> ToggleFavorite(
        int recipeId,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // Existence pre-check FIRST — a bare ToggleFavorite inserts a UserFavorite directly, so a missing or
        // cross-household recipe id would FK-violation/500. The household-scoped lookup makes it a clean 404 (M1).
        var recipe = await recipeService.GetRecipeAsync(user.HouseholdId, recipeId, ct);
        if (recipe is null) return Results.NotFound(new { message = "Recipe not found." });

        await recipeService.ToggleFavoriteAsync(user.UserId, user.HouseholdId, recipeId, ct);
        var isFavorite = await recipeService.IsFavoriteAsync(user.UserId, user.HouseholdId, recipeId, ct);
        return Results.Ok(new { isFavorite });
    }

    // ─── Ingredient entry helpers ──────────────────────────────────────────────────

    private static async Task<IResult> IngredientSuggestions(
        [FromQuery] string? prefix,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // The service guards <2 chars ⇒ []; pass through.
        var suggestions = await recipeService.GetIngredientSuggestionsAsync(user.HouseholdId, prefix ?? string.Empty, ct);
        return Results.Ok(suggestions);
    }

    private static async Task<IResult> ParseIngredient(
        ParseIngredientRequest req,
        ClaimsPrincipal principal,
        IIngredientParser parser,
        ICategoryInferenceService categoryInference,
        IRecipeProjectionService projection,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // ParseIngredient THROWS on empty input — guard BEFORE calling (a 400, not a 500).
        if (string.IsNullOrWhiteSpace(req.Text))
        {
            return Results.BadRequest(new { message = "Ingredient text is required." });
        }

        var parsed = parser.ParseIngredient(req.Text);
        var category = categoryInference.InferCategory(parsed.Name);
        return Results.Ok(projection.ToParsed(parsed, category));
    }

    private static async Task<IResult> ParseIngredients(
        ParseIngredientsRequest req,
        ClaimsPrincipal principal,
        IIngredientParser parser,
        ICategoryInferenceService categoryInference,
        IRecipeProjectionService projection,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var parsed = (req.Lines ?? new List<string>())
            .Where(l => !string.IsNullOrWhiteSpace(l))
            .Select(l =>
            {
                var p = parser.ParseIngredient(l);
                return projection.ToParsed(p, categoryInference.InferCategory(p.Name));
            })
            .ToList();
        return Results.Ok(parsed);
    }

    private static async Task<IResult> GetCategories(
        ClaimsPrincipal principal,
        ICategoryService categoryService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var categories = await categoryService.GetCategoriesAsync(user.HouseholdId, cancellationToken: ct);
        return Results.Ok(categories.Select(c => new CategoryDto(c.Name)).ToList());
    }

    // ─── Images ────────────────────────────────────────────────────────────────────

    private static async Task<IResult> UploadImage(
        IFormFile? file,
        ClaimsPrincipal principal,
        IImageService imageService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (file is null || file.Length == 0)
        {
            return Results.BadRequest(new { message = "No file uploaded." });
        }

        try
        {
            var path = await imageService.SaveImageAsync(file, user.HouseholdId, ct);
            return Results.Created(path, new { imagePath = path });
        }
        catch (InvalidOperationException ex)
        {
            // Size / extension / content-type validation failures throw InvalidOperationException → 400.
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> ListImages(
        ClaimsPrincipal principal,
        IImageService imageService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var images = await imageService.ListImagesAsync(user.HouseholdId, ct);
        return Results.Ok(images.ToArray());
    }

    // ─── Import ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> ImportRecipe(
        ImportRequest req,
        ClaimsPrincipal principal,
        IRecipeImportService importService,
        IRecipeService recipeService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Url))
        {
            return Results.BadRequest(new { message = "A recipe URL is required." });
        }

        // Endpoint-level duplicate detection (exact SourceUrl match; the global filter excludes deleted). `force`
        // bypasses ONLY this check (ImportFromUrlAsync has no force param) — mirrors ImportRecipeDialog.
        if (!req.Force)
        {
            await using var ctx = await dbFactory.CreateDbContextAsync(ct);
            var existing = await ctx.Recipes
                .Where(r => r.HouseholdId == user.HouseholdId && r.SourceUrl == req.Url)
                .Select(r => new { r.RecipeId, r.Name })
                .FirstOrDefaultAsync(ct);

            if (existing is not null)
            {
                return Results.Ok(new RecipeImportResultDto(
                    Success: false,
                    RecipeId: null,
                    ErrorMessage: $"This recipe has already been imported as \"{existing.Name}\".",
                    ErrorType: null,
                    ExistingRecipeId: existing.RecipeId,
                    ExistingRecipeName: existing.Name,
                    PartialData: null));
            }
        }

        var result = await importService.ImportFromUrlAsync(req.Url, user.HouseholdId, user.UserId, ct);

        if (result.Success && result.Recipe is not null)
        {
            // ImportFromUrlAsync returns an UNSAVED entity — persist it, then return the assigned id.
            var created = await recipeService.CreateRecipeAsync(result.Recipe, ct);
            return Results.Ok(new RecipeImportResultDto(
                Success: true, RecipeId: created.RecipeId, ErrorMessage: null, ErrorType: null,
                ExistingRecipeId: null, ExistingRecipeName: null, PartialData: null));
        }

        return Results.Ok(new RecipeImportResultDto(
            Success: false,
            RecipeId: null,
            ErrorMessage: result.ErrorMessage,
            ErrorType: result.ErrorType.ToString(),
            ExistingRecipeId: null,
            ExistingRecipeName: null,
            PartialData: MapPartial(result.PartialData)));
    }

    // ─── Connected households ──────────────────────────────────────────────────────

    private static async Task<IResult> GetConnections(
        ClaimsPrincipal principal,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var connections = await connectionService.GetConnectedHouseholdsAsync(user.HouseholdId, ct);
        return Results.Ok(connections.Select(c => new ConnectedHouseholdDto(c.HouseholdId, c.HouseholdName)).ToList());
    }

    private static async Task<IResult> ListConnectedRecipes(
        int chId,
        [FromQuery] string? q,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IRecipeProjectionService projection,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (!await connectionService.AreHouseholdsConnectedAsync(user.HouseholdId, chId, ct))
        {
            return Forbidden();
        }

        var recipes = await recipeService.GetRecipesFromConnectedHouseholdAsync(user.HouseholdId, chId, q, ct);
        var items = recipes.Select(projection.ToListItem).ToList();
        // Same shape as the own list (favorites always empty for a connected household).
        return Results.Ok(new RecipeListDto(items, Array.Empty<int>()));
    }

    private static async Task<IResult> GetConnectedRecipe(
        int chId,
        int recipeId,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IRecipeProjectionService projection,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (!await connectionService.AreHouseholdsConnectedAsync(user.HouseholdId, chId, ct))
        {
            return Forbidden();
        }

        // Reuse the household-scoped fetch with the CONNECTED id (connection-gated above) — the only single-fetch.
        var recipe = await recipeService.GetRecipeAsync(chId, recipeId, ct);
        if (recipe is null) return Results.NotFound(new { message = "Recipe not found." });

        // Strip author for connected reads (privacy — mirrors GetRecipesFromConnectedHouseholdAsync excluding CreatedBy).
        return Results.Ok(projection.ToFull(recipe, includeAuthor: false));
    }

    private static async Task<IResult> CopyConnectedRecipe(
        int chId,
        int recipeId,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IHouseholdConnectionService connectionService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (!await connectionService.AreHouseholdsConnectedAsync(user.HouseholdId, chId, ct))
        {
            return Forbidden();
        }

        // Validate the source exists in the connected household before copying (clean 404, no NRE in the service).
        var source = await recipeService.GetRecipeAsync(chId, recipeId, ct);
        if (source is null) return Results.NotFound(new { message = "Recipe not found." });

        var copy = await recipeService.CopyRecipeFromConnectedHouseholdAsync(chId, recipeId, user.HouseholdId, user.UserId, ct);
        return Results.Created($"/api/recipes/{copy.RecipeId}", new { recipeId = copy.RecipeId });
    }

    // ─── Drafts ────────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetDraft(
        [FromQuery] int? recipeId,
        ClaimsPrincipal principal,
        IDraftService draftService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // recipeId omitted ⇒ "new recipe" draft ⇒ 0 sentinel (matches SaveDraft).
        var draft = await draftService.GetDraftAsync(user.HouseholdId, user.UserId, recipeId ?? 0, ct);
        // 204 when there's no draft (the island's request<T> treats No-Content as null); 200 + body otherwise.
        return draft is null ? Results.NoContent() : Results.Ok(draft);
    }

    private static async Task<IResult> SaveDraft(
        SaveDraftRequest req,
        ClaimsPrincipal principal,
        IDraftService draftService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Draft name is required." });
        }

        // Map the flat request to DraftService's RecipeDraftData (which it serializes to the draft row).
        var draft = new RecipeDraftData(
            req.Name, req.Description, req.Instructions, req.ImagePath, req.SourceUrl,
            req.Servings, req.PrepTimeMinutes, req.CookTimeMinutes,
            (req.Ingredients ?? new List<DraftIngredientBody>())
                .Select(i => new IngredientDraftData(i.Name, i.Quantity, i.Unit, i.Category, i.Notes, i.GroupName, i.SortOrder))
                .ToList());

        // recipeId null ⇒ "new recipe" draft ⇒ 0 sentinel (RecipeDraft's composite PK can't take null).
        await draftService.SaveDraftAsync(user.HouseholdId, user.UserId, req.RecipeId ?? 0, draft, ct);
        return Results.NoContent();
    }

    private static async Task<IResult> DeleteDraft(
        [FromQuery] int? recipeId,
        ClaimsPrincipal principal,
        IDraftService draftService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        await draftService.DeleteDraftAsync(user.HouseholdId, user.UserId, recipeId ?? 0, ct);
        return Results.NoContent();
    }

    // ─── Helpers ───────────────────────────────────────────────────────────────────

    /// <summary>A 403 with a non-empty body (so the status-code re-execute leaves it as a clean 403).</summary>
    private static IResult Forbidden() =>
        Results.Json(new { message = "Households are not connected." }, statusCode: StatusCodes.Status403Forbidden);

    /// <summary>Map a write request to a <see cref="Recipe"/>. RecipeId 0 ⇒ create (service assigns the id).</summary>
    private static Recipe MapToRecipe(RecipeWriteRequest req, int householdId, int recipeId) => new()
    {
        HouseholdId = householdId,
        RecipeId = recipeId,
        Name = req.Name.Trim(),
        Description = NullIfBlank(req.Description),
        Instructions = NullIfBlank(req.Instructions),
        SourceUrl = NullIfBlank(req.SourceUrl),
        Servings = req.Servings,
        PrepTimeMinutes = req.PrepTimeMinutes,
        CookTimeMinutes = req.CookTimeMinutes,
        RecipeType = req.RecipeType,
        ImagePath = NullIfBlank(req.ImagePath),
        Ingredients = (req.Ingredients ?? new List<RecipeIngredientWrite>())
            .Select((ing, i) => new RecipeIngredient
            {
                Name = ing.Name.Trim(),
                Quantity = ing.Quantity,
                Unit = NullIfBlank(ing.Unit),
                Category = string.IsNullOrWhiteSpace(ing.Category) ? "Pantry" : ing.Category.Trim(),
                Notes = NullIfBlank(ing.Notes),
                GroupName = NullIfBlank(ing.GroupName),
                SortOrder = i, // recompute 0..n in list order
            })
            .ToList(),
    };

    private static PartialRecipeDataDto? MapPartial(PartialRecipeData? p) => p is null ? null : new(
        p.Name, p.Description, p.Instructions, p.IngredientStrings, p.ImageUrl,
        p.PrepTimeMinutes, p.CookTimeMinutes, p.Servings);

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    // ─── Request DTOs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Create/update body. <see cref="ImagePath"/> is a path already returned by POST /images.
    /// <see cref="Version"/> is the xmin token from GET, enforced on PUT (stale ⇒ 409); ignored on POST and
    /// skipped when null (a client without a token keeps last-write-wins).
    /// </summary>
    public sealed record RecipeWriteRequest(
        string Name,
        string? Description,
        string? Instructions,
        string? SourceUrl,
        int? Servings,
        int? PrepTimeMinutes,
        int? CookTimeMinutes,
        RecipeType RecipeType,
        string? ImagePath,
        IReadOnlyList<RecipeIngredientWrite> Ingredients,
        uint? Version = null);

    public sealed record RecipeIngredientWrite(
        string Name,
        decimal? Quantity,
        string? Unit,
        string Category,
        string? Notes,
        string? GroupName,
        int SortOrder);

    public sealed record ParseIngredientRequest(string Text);

    public sealed record ParseIngredientsRequest(IReadOnlyList<string> Lines);

    public sealed record ImportRequest(string Url, bool Force = false);

    /// <summary>
    /// Draft autosave body — FLAT (recipeId + the draft fields), mapped to DraftService's
    /// <see cref="RecipeDraftData"/> in the handler. The island sends <c>{ recipeId, ...draftFields }</c>
    /// (recipeId omitted/null for a new-recipe draft → 0 sentinel server-side); GET /draft returns the draft
    /// fields (RecipeDraftData).
    /// </summary>
    public sealed record SaveDraftRequest(
        int? RecipeId,
        string Name,
        string? Description,
        string? Instructions,
        string? ImagePath,
        string? SourceUrl,
        int? Servings,
        int? PrepTimeMinutes,
        int? CookTimeMinutes,
        IReadOnlyList<DraftIngredientBody> Ingredients);

    public sealed record DraftIngredientBody(
        string Name,
        decimal? Quantity,
        string? Unit,
        string Category,
        string? Notes,
        string? GroupName,
        int SortOrder);
}
