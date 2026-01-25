using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IMealPlanService
{
    Task<MealPlan> GetOrCreateMealPlanAsync(int householdId, DateOnly weekStart, CancellationToken cancellationToken = default);
    Task<MealPlanEntry> AddMealAsync(int householdId, DateOnly date, MealType mealType, int? recipeId, string? customMealName, string? notes = null, int? userId = null, CancellationToken cancellationToken = default);
    Task RemoveMealAsync(int householdId, int mealPlanId, int entryId, CancellationToken cancellationToken = default);
    DateOnly GetWeekStartDate(DateOnly date);
    DateOnly[] GetWeekDays(DateOnly weekStart);
}
