namespace FamilyCoordinationApp.Data.Entities;

public class Category
{
    public int HouseholdId { get; set; }
    public int CategoryId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string IconEmoji { get; set; } = string.Empty;  // e.g., "meat_on_bone", "leafy_green"
    public string Color { get; set; } = "#808080";  // Hex color
    public int SortOrder { get; set; }
    public bool IsDefault { get; set; }  // True for system defaults
    public bool IsDeleted { get; set; }
    public DateTime? DeletedAt { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
}
