namespace FamilyCoordinationApp.Data.Entities;

public class Room
{
    public int HouseholdId { get; set; }
    public int RoomId { get; set; }
    public string Name { get; set; } = string.Empty;
    public string Icon { get; set; } = string.Empty;  // Emoji or short code
    public string? PhotoPath { get; set; }
    public int SortOrder { get; set; }
    public DateTime CreatedAt { get; set; }  // UTC

    // Navigation
    public Household Household { get; set; } = default!;
}
