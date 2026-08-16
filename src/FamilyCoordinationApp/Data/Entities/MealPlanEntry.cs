using System.ComponentModel.DataAnnotations;

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

    /// <summary>
    /// How many people this meal is being cooked for. NULL means "as the recipe is written" — shopping-list
    /// generation then scales that recipe's ingredients by <c>Servings / Recipe.Servings</c>, and by nothing at
    /// all while this is null. Opt-in on purpose: a recipe deliberately batch-sized for leftovers must not be
    /// silently shrunk to the household's headcount.
    /// </summary>
    public int? Servings { get; set; }

    // Change tracking fields
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }

    // Concurrency token (maps to PostgreSQL xmin)
    [Timestamp]
    public uint Version { get; set; }

    // Navigation
    public MealPlan MealPlan { get; set; } = default!;
    public Recipe? Recipe { get; set; }
    public User? UpdatedBy { get; set; }
}
