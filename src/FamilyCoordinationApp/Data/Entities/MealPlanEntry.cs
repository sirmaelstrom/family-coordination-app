namespace FamilyCoordinationApp.Data.Entities;

public enum MealType { Breakfast, Lunch, Dinner, Snack }

public class MealPlanEntry
{
    public int HouseholdId { get; set; }
    public int MealPlanId { get; set; }
    public int EntryId { get; set; }
    public DateOnly Date { get; set; }
    public MealType MealType { get; set; }
    public int? RecipeId { get; set; }  // Null for custom meals
    public string? CustomMealName { get; set; }  // "Leftovers", "Eating out"
    public string? Notes { get; set; }

    // Navigation
    public MealPlan MealPlan { get; set; } = default!;
    public Recipe? Recipe { get; set; }
}
