using System.Globalization;
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
/// Minimal-API surface for the meal-plan island (strangler — mirrors <see cref="ChoresEndpoints"/>): a
/// <c>/api/meal-plan</c> group behind <c>.RequireAuthorization().DisableAntiforgery()</c>, every handler
/// resolving the HouseholdId/UserId from the authenticated caller (M1, never client-supplied) via
/// <see cref="UserContextResolver"/>. Writes delegate to <see cref="IMealPlanService"/> / <see cref="IRecipeService"/>;
/// the board read + per-entry projection delegate to <see cref="IMealPlanBoardService"/> (ONE projection — no
/// card/response drift, M9).
///
/// <para>Entry mutations carry the row's xmin token and answer <b>409</b> on a stale one (PR #95, the house
/// pattern — see <see cref="FamilyCoordinationApp.Services.MealPlanConflictException"/>): move, set-servings
/// and remove all take a <c>version</c>, remove carrying it in the DELETE body as the chores DELETE does.
/// <b>Add is the exception</b>, and only because it creates the row — there is no prior version to be stale.
/// A remove of a missing entry → 404 (the island treats any 4xx as a non-retryable refetch).</para>
/// </summary>
public static class MealPlanEndpoints
{
    public static IEndpointRouteBuilder MapMealPlanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/api/meal-plan")
            .RequireAuthorization()
            // Mirrors the shopping-list/chores island groups: the island calls these with same-origin
            // credentialed fetch + JSON bodies (never HTML form posts) and the auth cookie is SameSite, so the
            // antiforgery token — which the SPA island can't readily supply — is not the CSRF control here.
            // Kept consistent with the other island endpoint groups.
            .DisableAntiforgery();

        group.MapGet("/board", GetBoard);
        group.MapPost("/entries", AddEntry);
        group.MapPatch("/entries/{mealPlanId:int}/{entryId:int}", MoveEntry);
        group.MapPatch("/entries/{mealPlanId:int}/{entryId:int}/servings", SetEntryServings);
        group.MapDelete("/entries/{mealPlanId:int}/{entryId:int}", RemoveEntry);

        group.MapGet("/recipes", SearchRecipes);
        group.MapPost("/recipes", QuickCreateRecipe);
        group.MapGet("/recipes/{recipeId:int}", GetRecipeDetail);

        return app;
    }

    // ─── Board ──────────────────────────────────────────────────────────────────

    private static async Task<IResult> GetBoard(
        [FromQuery] string? weekStart,
        ClaimsPrincipal principal,
        IMealPlanService mealPlanService,
        IMealPlanBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // Snap to the week's Monday SERVER-side (the client may send any date in the week; this is the only
        // authority on the week boundary). Missing/unparseable ⇒ current week (matches the Blazor page's
        // DateTime.Today). The island always sends a "YYYY-MM-DD", so the fallback is rarely hit.
        var baseDate = DateOnly.TryParseExact(weekStart, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            ? parsed
            : DateOnly.FromDateTime(DateTime.Today);
        var monday = mealPlanService.GetWeekStartDate(baseDate);

        var board = await boardService.GetBoardAsync(user.HouseholdId, monday, ct);
        return Results.Ok(board);
    }

    // ─── Entries (add is versionless; move / servings / remove carry the xmin token) ──────────────

    private static async Task<IResult> AddEntry(
        AddEntryRequest req,
        ClaimsPrincipal principal,
        IMealPlanService mealPlanService,
        IMealPlanBoardService boardService,
        IRecipeService recipeService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        // XOR: exactly one of recipeId / customMealName. The service also guards (throws
        // InvalidOperationException), but validate here for a clean 400 rather than a 500.
        var hasRecipe = req.RecipeId.HasValue;
        var hasCustom = !string.IsNullOrWhiteSpace(req.CustomMealName);
        if (hasRecipe == hasCustom)
        {
            return Results.BadRequest(new { message = "Provide exactly one of recipeId or customMealName." });
        }

        // Validate recipe ownership BEFORE creating anything (council R1). The household-scoped lookup makes a
        // cross-household recipe id a clean not-found (M1, no leak), and validating up front avoids both an
        // FK-violation 500 AND an orphan MealPlan row — GetOrCreateMealPlanAsync commits the week's plan in its
        // own SaveChanges, so a bad recipeId reaching the entry insert would leave an empty plan behind. The
        // loaded recipe is reused for the response projection (AddMealAsync doesn't load the nav).
        Recipe? recipe = null;
        if (req.RecipeId.HasValue)
        {
            recipe = await recipeService.GetRecipeAsync(user.HouseholdId, req.RecipeId.Value, ct);
            if (recipe is null)
            {
                return Results.NotFound(new { message = "Recipe not found." });
            }
        }

        var entry = await mealPlanService.AddMealAsync(
            user.HouseholdId,
            req.Date,
            req.MealType,
            req.RecipeId,
            hasCustom ? req.CustomMealName!.Trim() : null,
            string.IsNullOrWhiteSpace(req.Notes) ? null : req.Notes.Trim(),
            user.UserId,
            ct);

        var dto = boardService.ProjectEntry(entry, recipe);
        return Results.Created($"/api/meal-plan/entries/{entry.MealPlanId}/{entry.EntryId}", dto);
    }

    private static async Task<IResult> MoveEntry(
        int mealPlanId,
        int entryId,
        MoveEntryRequest req,
        ClaimsPrincipal principal,
        IMealPlanService mealPlanService,
        IMealPlanBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            // Household-scoped move to another same-week slot (drag-to-assign). The service loads the
            // Recipe nav, so the response reuses the ONE board projection (M9) with no extra query.
            var entry = await mealPlanService.MoveMealAsync(
                user.HouseholdId, mealPlanId, entryId, req.Date, req.MealType, req.Version, user.UserId, ct);
            return Results.Ok(boardService.ProjectEntry(entry, entry.Recipe));
        }
        catch (MealPlanConflictException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException)
        {
            // Non-empty body — see RemoveEntry (a specific message beats the generic /api backfill).
            return Results.NotFound(new { message = "Meal entry not found." });
        }
        catch (ArgumentException ex)
        {
            // Cross-week target / duplicate-in-slot — clean 400 with the reason (messages are ours).
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> SetEntryServings(
        int mealPlanId,
        int entryId,
        SetEntryServingsRequest req,
        ClaimsPrincipal principal,
        IMealPlanService mealPlanService,
        IMealPlanBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            // The service loads the Recipe nav, so the response reuses the ONE board projection (M9).
            var entry = await mealPlanService.SetMealServingsAsync(
                user.HouseholdId, mealPlanId, entryId, req.Servings, req.Version, user.UserId, ct);
            return Results.Ok(boardService.ProjectEntry(entry, entry.Recipe));
        }
        catch (MealPlanConflictException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException)
        {
            return Results.NotFound(new { message = "Meal entry not found." });
        }
        catch (ArgumentException ex)
        {
            return Results.BadRequest(new { message = ex.Message });
        }
    }

    private static async Task<IResult> RemoveEntry(
        int mealPlanId,
        int entryId,
        [FromBody] EntryVersionRequest req,
        ClaimsPrincipal principal,
        IMealPlanService mealPlanService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        try
        {
            // Household-scoped — a cross-household id finds nothing ⇒ throws ⇒ 404 (M1).
            await mealPlanService.RemoveMealAsync(user.HouseholdId, mealPlanId, entryId, req.Version, ct);
            return Results.NoContent();
        }
        catch (MealPlanConflictException ex)
        {
            return Results.Conflict(new { message = ex.Message });
        }
        catch (InvalidOperationException)
        {
            // Non-empty body so callers receive a specific message instead of the generic /api backfill.
            return Results.NotFound(new { message = "Meal entry not found." });
        }
    }

    // ─── Recipes (picker search / quick-create / detail) ─────────────────────────

    private static async Task<IResult> SearchRecipes(
        [FromQuery] string? q,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IMealPlanBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var recipes = await recipeService.GetRecipesAsync(user.HouseholdId, q, ct);
        var summaries = recipes.Select(boardService.ToRecipeSummary).ToList();
        return Results.Ok(summaries);
    }

    private static async Task<IResult> QuickCreateRecipe(
        QuickCreateRecipeRequest req,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IMealPlanBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        if (string.IsNullOrWhiteSpace(req.Name))
        {
            return Results.BadRequest(new { message = "Recipe name is required." });
        }

        var recipe = new Recipe
        {
            HouseholdId = user.HouseholdId,
            Name = req.Name.Trim(),
            RecipeType = req.RecipeType,
            CreatedByUserId = user.UserId,
            CreatedAt = DateTime.UtcNow,
        };

        var created = await recipeService.CreateRecipeAsync(recipe, ct);
        var summary = boardService.ToRecipeSummary(created);
        return Results.Created($"/api/meal-plan/recipes/{created.RecipeId}", summary);
    }

    private static async Task<IResult> GetRecipeDetail(
        int recipeId,
        ClaimsPrincipal principal,
        IRecipeService recipeService,
        IMealPlanBoardService boardService,
        IDbContextFactory<ApplicationDbContext> dbFactory,
        CancellationToken ct)
    {
        var user = await UserContextResolver.ResolveUserAsync(principal, dbFactory, ct);
        if (user is null) return Results.Unauthorized();

        var recipe = await recipeService.GetRecipeAsync(user.HouseholdId, recipeId, ct);
        // Non-empty body so callers receive a specific 404 message instead of the generic /api backfill.
        if (recipe is null) return Results.NotFound(new { message = "Recipe not found." });

        return Results.Ok(boardService.ToRecipeDetail(recipe));
    }

    // ─── Request DTOs ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Add a meal to a slot. <see cref="Date"/> is the slot's calendar position ("YYYY-MM-DD"); the server
    /// derives the week from it. Supply EXACTLY one of <see cref="RecipeId"/> / <see cref="CustomMealName"/>.
    /// No concurrency token: this CREATES the entry, so there is no prior version to be stale. Every
    /// other entry mutation carries one.
    /// </summary>
    public sealed record AddEntryRequest(
        DateOnly Date,
        MealType MealType,
        int? RecipeId,
        string? CustomMealName,
        string? Notes);

    /// <summary>
    /// Move an entry to another slot in the SAME week (drag-to-assign). <see cref="Date"/> must fall inside
    /// the entry's plan week — a cross-week target is a 400 (a plan owns exactly one week).
    /// <see cref="Version"/> is the entry's xmin token from the board; a stale one is a 409.
    /// </summary>
    public sealed record MoveEntryRequest(DateOnly Date, MealType MealType, uint Version);

    /// <summary>The entry's xmin token, for a mutation that carries no other payload (remove).</summary>
    public sealed record EntryVersionRequest(uint Version);

    /// <summary>
    /// How many people this meal is being cooked for. <c>null</c> clears the override, putting the meal back to
    /// the recipe as written; a non-positive number is a 400. Shopping-list generation scales this entry's
    /// ingredients by <c>Servings / Recipe.Servings</c>, and by nothing when either side is missing.
    /// <see cref="Version"/> is the entry's xmin token from the board; a stale one is a 409.
    /// </summary>
    public sealed record SetEntryServingsRequest(int? Servings, uint Version);

    /// <summary>Quick-create a bare recipe from the picker's "New Recipe" tab (details added later).</summary>
    public sealed record QuickCreateRecipeRequest(string Name, RecipeType RecipeType);
}
