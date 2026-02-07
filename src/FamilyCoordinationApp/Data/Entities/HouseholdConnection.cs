using System.ComponentModel.DataAnnotations;

namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Bi-directional connection between two households for recipe sharing.
/// HouseholdId1 is always less than HouseholdId2 (enforced by check constraint)
/// to prevent duplicate connections regardless of who initiated.
/// </summary>
public class HouseholdConnection
{
    public int HouseholdId1 { get; set; }
    public int HouseholdId2 { get; set; }
    public DateTime ConnectedAt { get; set; }
    public int? InitiatedByUserId { get; set; }
    public int? AcceptedByUserId { get; set; }

    // Navigation
    public Household Household1 { get; set; } = default!;
    public Household Household2 { get; set; } = default!;
    public User? InitiatedBy { get; set; }
    public User? AcceptedBy { get; set; }
}
