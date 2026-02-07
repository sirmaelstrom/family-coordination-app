using System.ComponentModel.DataAnnotations;

namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Short-lived invite code for establishing household connections.
/// Codes are 6-char from charset ABCDEFGHJKLMNPQRSTUVWXYZ23456789 (excludes ambiguous 0/1/O/I).
/// </summary>
public class HouseholdInvite
{
    public int Id { get; set; }
    public int HouseholdId { get; set; }
    public string InviteCode { get; set; } = string.Empty;
    public int CreatedByUserId { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime ExpiresAt { get; set; }
    public bool IsUsed { get; set; }
    public DateTime? UsedAt { get; set; }
    public int? UsedByHouseholdId { get; set; }
    public int? UsedByUserId { get; set; }

    // Concurrency token (maps to PostgreSQL xmin)
    [Timestamp]
    public uint Version { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
    public User CreatedBy { get; set; } = default!;
    public Household? UsedByHousehold { get; set; }
    public User? UsedByUser { get; set; }
}
