namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Junction table for user recipe favorites.
/// </summary>
public class UserFavorite
{
    public int UserId { get; set; }
    public int HouseholdId { get; set; }
    public int RecipeId { get; set; }
    public DateTime CreatedAt { get; set; }

    // Navigation
    public User User { get; set; } = default!;
    public Recipe Recipe { get; set; } = default!;
}
