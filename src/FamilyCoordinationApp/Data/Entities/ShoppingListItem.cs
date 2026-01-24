using System.ComponentModel.DataAnnotations;

namespace FamilyCoordinationApp.Data.Entities;

public class ShoppingListItem
{
    public int HouseholdId { get; set; }
    public int ShoppingListId { get; set; }
    public int ItemId { get; set; }
    public string Name { get; set; } = string.Empty;
    public decimal? Quantity { get; set; }
    public string? Unit { get; set; }
    public string Category { get; set; } = "Pantry";
    public bool IsChecked { get; set; }
    public int? AddedByUserId { get; set; }
    public DateTime AddedAt { get; set; }
    public DateTime? CheckedAt { get; set; }

    // Consolidation tracking
    public string? SourceRecipes { get; set; }  // Comma-separated recipe names, e.g., "Pancakes, Mac & Cheese"
    public string? OriginalUnits { get; set; }  // If units were converted, e.g., "1 cup + 8 fl oz"
    public bool IsManuallyAdded { get; set; }  // True if user added, false if from meal plan
    public decimal? QuantityDelta { get; set; }  // User adjustment for preserving during regeneration (+1, -0.5, etc.)
    public string? RecipeIngredientIds { get; set; }  // Comma-separated IDs (format: "1:2:3,1:2:5" for HouseholdId:RecipeId:IngredientId)
    public int SortOrder { get; set; }  // For custom ordering within category

    // Change tracking fields
    public DateTime? UpdatedAt { get; set; }
    public int? UpdatedByUserId { get; set; }

    // Concurrency token (maps to PostgreSQL xmin)
    [Timestamp]
    public uint Version { get; set; }

    // Navigation
    public ShoppingList ShoppingList { get; set; } = default!;
    public User? AddedBy { get; set; }
    public User? UpdatedBy { get; set; }
}
