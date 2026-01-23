namespace FamilyCoordinationApp.Data.Entities;

public class ShoppingList
{
    public int HouseholdId { get; set; }
    public int ShoppingListId { get; set; }
    public string Name { get; set; } = string.Empty;
    public int? MealPlanId { get; set; }  // If generated from meal plan
    public DateTime CreatedAt { get; set; }
    public bool IsArchived { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
    public MealPlan? MealPlan { get; set; }
    public ICollection<ShoppingListItem> Items { get; set; } = new List<ShoppingListItem>();
}
