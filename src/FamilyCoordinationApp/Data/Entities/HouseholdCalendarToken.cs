namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// A rotatable, read-only capability for a household's calendar feed. Only the SHA-256 hash of the secret is
/// persisted, so a database read cannot recover a usable feed URL.
/// </summary>
public class HouseholdCalendarToken
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string TokenHash { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }
    public DateTime? RevokedAt { get; set; }
    public uint Version { get; set; }

    public Household Household { get; set; } = default!;
}
