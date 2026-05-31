namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Append-only audit event for a chore (claim/drop/handoff/auto-release lifecycle). For
/// <see cref="ChoreEventType.AutoReleased"/>, <see cref="ActorUserId"/> is the lapsed claimer
/// whose claim expired (council M16).
/// </summary>
public class ChoreEvent
{
    public int HouseholdId { get; set; }
    public int ChoreId { get; set; }
    public int EventId { get; set; }

    public ChoreEventType Type { get; set; }
    public int ActorUserId { get; set; }
    public int? TargetUserId { get; set; }
    public DateTime At { get; set; }  // UTC

    // Navigation
    public Chore Chore { get; set; } = default!;
    public User Actor { get; set; } = default!;
    public User? Target { get; set; }
}
