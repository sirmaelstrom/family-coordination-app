using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Interfaces;

/// <summary>
/// Read + projection for the meal-plan island board (mirrors <c>IChoreBoardService</c>). The ONE place the
/// board read model and the per-entry mutation response are projected, so the slot card and the add-entry
/// response cannot drift (M9). All reads are household-scoped (M1).
/// </summary>
public interface IMealPlanBoardService
{
    /// <summary>
    /// The read-only board for a week. <paramref name="weekStart"/> MUST already be the week's Monday
    /// (the endpoint snaps it via <see cref="IMealPlanService.GetWeekStartDate"/>). No plan for the week ⇒
    /// <c>MealPlanId = null, Entries = []</c> (a GET never creates a plan).
    /// </summary>
    Task<MealPlanBoardDto> GetBoardAsync(int householdId, DateOnly weekStart, CancellationToken ct = default);

    /// <summary>
    /// Project a single entry. <paramref name="recipe"/> is the entry's recipe when it is a recipe meal
    /// (the caller supplies it — the entity nav is not always loaded), or <c>null</c> for a custom meal.
    /// </summary>
    MealPlanEntryDto ProjectEntry(MealPlanEntry entry, Recipe? recipe);

    /// <summary>The lean recipe summary a slot card / picker row renders.</summary>
    MealRecipeSummaryDto ToRecipeSummary(Recipe recipe);

    /// <summary>Full read-only recipe detail (instructions pre-sanitized to HTML).</summary>
    RecipeDetailDto ToRecipeDetail(Recipe recipe);
}
