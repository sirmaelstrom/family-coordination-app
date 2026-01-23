namespace FamilyCoordinationApp.Data.Entities;

public class RecipeIngredient
{
    public int HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public int IngredientId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Category { get; set; } = "Pantry"; // Meat, Produce, Dairy, Pantry, Spices
    public int SortOrder { get; set; }

    // Navigation
    public Recipe Recipe { get; set; } = default!;
}
