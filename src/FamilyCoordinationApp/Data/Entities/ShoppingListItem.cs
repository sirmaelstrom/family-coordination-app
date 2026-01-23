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

    // Navigation
    public ShoppingList ShoppingList { get; set; } = default!;
    public User? AddedBy { get; set; }
}
