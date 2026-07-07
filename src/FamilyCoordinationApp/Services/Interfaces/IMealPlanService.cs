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
    /// </summary>
    Task<MealPlanEntry> MoveMealAsync(int householdId, int mealPlanId, int entryId, DateOnly newDate, MealType newMealType, int? userId = null, CancellationToken cancellationToken = default);

    Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, CancellationToken cancellationToken = default);
    DateOnly GetWeekStartDate(DateOnly date);
    DateOnly[] GetWeekDays(DateOnly weekStart);
}
