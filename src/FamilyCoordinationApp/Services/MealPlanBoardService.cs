using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;
using FamilyCoordinationApp.Services.Interfaces;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Read + projection for the meal-plan island board (mirrors <see cref="ChoreBoardService"/>). Owns the ONE
/// entry projection shared by the board read and the add-entry response so the two cannot drift (M9). All
/// reads create short-lived contexts via the factory (Blazor Server circuit safety) and filter by
/// <c>HouseholdId</c> (M1).
/// </summary>
public class MealPlanBoardService(IDbContextFactory<ApplicationDbContext> dbFactory) : IMealPlanBoardService
{
    public async Task<MealPlanBoardDto> GetBoardAsync(int householdId, DateOnly weekStart, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        // Read-only — a GET never creates a plan (creation happens lazily on the first AddMeal). Filtered
        // include orders the entries (Date, then MealType) the same way MealPlanService does.
        var plan = await context.MealPlans
            .AsNoTracking()
            .Where(mp => mp.HouseholdId == householdId && mp.WeekStartDate == weekStart)
            .Include(mp => mp.Entries.OrderBy(e => e.Date).ThenBy(e => e.MealType))
                .ThenInclude(e => e.Recipe)
            .FirstOrDefaultAsync(ct);

        if (plan is null)
        {
            return new MealPlanBoardDto(weekStart, null, Array.Empty<MealPlanEntryDto>());
        }

        var entries = plan.Entries
            .Select(e => ProjectEntry(e, e.Recipe))
            .ToList();

        return new MealPlanBoardDto(weekStart, plan.MealPlanId, entries);
    }

    public MealPlanEntryDto ProjectEntry(MealPlanEntry entry, Recipe? recipe) => new(
        entry.MealPlanId,
        entry.EntryId,
        entry.Date,
        entry.MealType,
        recipe is null ? null : ToRecipeSummary(recipe),
        entry.CustomMealName,
        entry.Notes);

    public MealRecipeSummaryDto ToRecipeSummary(Recipe recipe) =>
        new(recipe.RecipeId, recipe.Name, recipe.ImagePath, recipe.RecipeType);

    public RecipeDetailDto ToRecipeDetail(Recipe recipe) => new(
        recipe.RecipeId,
        recipe.Name,
        recipe.ImagePath,
        recipe.RecipeType,
        recipe.PrepTimeMinutes,
        recipe.CookTimeMinutes,
        recipe.Servings,
        MarkdownHelper.ToSafeHtml(recipe.Instructions),
        recipe.Ingredients
            .OrderBy(i => i.SortOrder)
            .Select(i => new RecipeIngredientDto(i.Quantity, i.Unit, i.Name, i.Notes, i.SortOrder))
            .ToList());
}
