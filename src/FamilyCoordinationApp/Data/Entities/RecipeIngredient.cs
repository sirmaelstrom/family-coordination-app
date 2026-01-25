using FamilyCoordinationApp.Constants;

namespace FamilyCoordinationApp.Data.Entities;

public class RecipeIngredient
{
    public int HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public int IngredientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Category { get; set; } = CategoryDefaults.DefaultCategory; // References Category.Name
    public string? Notes { get; set; }
    public string? GroupName { get; set; }  // For ingredient sections like "For the sauce"
    public int SortOrder { get; set; }

    // Navigation
    public Recipe Recipe { get; set; } = default!;
}
