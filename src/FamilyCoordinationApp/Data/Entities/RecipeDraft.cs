namespace FamilyCoordinationApp.Data.Entities;

public class RecipeDraft
{
    public int HouseholdId { get; set; }
    public int UserId { get; set; }  // References User.Id
    public int? RecipeId { get; set; }  // Null for new recipes
    public string DraftJson { get; set; } = string.Empty;  // Serialized recipe data
    public DateTime UpdatedAt { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
    public User User { get; set; } = default!;
}
