namespace FamilyCoordinationApp.Data.Entities;

public class User
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string Email { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string? GoogleId { get; set; }
    public bool IsWhitelisted { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? LastLoginAt { get; set; }

    // Profile fields for avatar display
    public string? PictureUrl { get; set; }
    public string Initials { get; set; } = string.Empty;

    // Chores: the user's preferred default board lens id (null => Needs-attention). D18 / approved E2 exception.
    public string? ChoresDefaultView { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
}
