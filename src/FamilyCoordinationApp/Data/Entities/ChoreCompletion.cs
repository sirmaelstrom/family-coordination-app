namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Append-only completion log for a chore (P2). Carries the effort points snapshot at completion
/// time — the v1.1 equity substrate (MN3/MN4: the log ships now, the equity layer does not).
/// </summary>
public class ChoreCompletion
{
    public int HouseholdId { get; set; }
    public int ChoreId { get; set; }
    public int CompletionId { get; set; }

    public int CompletedByUserId { get; set; }
    public DateTime CompletedAt { get; set; }  // UTC
    public int EffortPointsSnapshot { get; set; }
    public string? Note { get; set; }
    public string? PhotoPath { get; set; }

    // Navigation
    public Chore Chore { get; set; } = default!;
    public User CompletedBy { get; set; } = default!;
}
