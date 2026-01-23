namespace FamilyCoordinationApp.Data.Entities;

public class User
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GoogleId { get; set; } = string.Empty;
    public bool IsWhitelisted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
}
