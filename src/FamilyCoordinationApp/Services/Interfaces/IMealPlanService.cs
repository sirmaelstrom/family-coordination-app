using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IMealPlanService
{
    Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStart, CancellationToken cancellationToken = default);
    Task<MealPlanEntry> AddMealAsync(int householdId, DateOnly date, MealType mealType, int? recipeId, string? customMealName, string? notes = null, int? userId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Move an existing entry to another slot (date × meal type) WITHIN its plan's week (drag-to-assign).
    /// Household-scoped (M1). Returns the updated entry with its <c>Recipe</c> nav loaded (for projection).
    /// Throws <see cref="InvalidOperationException"/> when the entry is not found and
    /// <see cref="ArgumentException"/> when the target date falls outside the plan's week or the target
    /// slot already holds the same meal (mirrors the AddMealAsync duplicate guard).
    /// Throws <see cref="MealPlanConflictException"/> when <paramref name="version"/> is stale.
    /// </summary>
    Task<MealPlanEntry> MoveMealAsync(int householdId, int mealPlanId, int entryId, DateOnly newDate, MealType newMealType, uint version, int? userId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Set how many people this meal is being cooked for; null clears the override back to "as the recipe is
    /// written". Shopping-list generation scales that entry's ingredients by <c>servings / Recipe.Servings</c>.
    /// Household-scoped: a cross-household id finds nothing and throws.
    /// Throws <see cref="MealPlanConflictException"/> when <paramref name="version"/> is stale.
    /// </summary>
    Task<MealPlanEntry> SetMealServingsAsync(int householdId, int mealPlanId, int entryId, int? servings, uint version, int? userId = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Remove an entry. Household-scoped (M1); throws <see cref="MealPlanConflictException"/> when
    /// <paramref name="version"/> is stale — deleting a row someone else just changed is the same class of
    /// silent loss as overwriting it.
    /// </summary>
    Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, uint version, CancellationToken cancellationToken = default);
    DateOnly GetWeekStartDate(DateOnly date);
}
