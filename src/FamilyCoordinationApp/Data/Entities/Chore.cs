using System.ComponentModel.DataAnnotations;

namespace FamilyCoordinationApp.Data.Entities;

public class Chore
{
    public int HouseholdId { get; set; }
    public int ChoreId { get; set; }

    public string Name { get; set; } = string.Empty;
    public string? Description { get; set; }

    // Optional room association (nullable FK -> Room).
    public int? RoomId { get; set; }

    // Recurrence configuration (the logic lives in WP-02/WP-04; these only hold it).
    public RecurrenceMode RecurrenceMode { get; set; }
    public int? IntervalDays { get; set; }
    public DateOnly? AnchorDate { get; set; }
    public ChoreDaysOfWeek? DaysOfWeek { get; set; }
    public int? DayOfMonth { get; set; }

    // Denormalized convenience (P2 — the only deliberate denormalization on Chore).
    public DateTime? LastCompletedAt { get; set; }  // UTC

    // Effort (P3 — named tier; EffortPoints is the v1.1 equity substrate, defaults to 1).
    public EffortTier EffortTier { get; set; }
    public int EffortPoints { get; set; } = 1;

    // Stored lifecycle state (council M15).
    public ChoreStatus Status { get; set; }

    // Authorship / ownership.
    public int EnteredByUserId { get; set; }
    public int? OwnerUserId { get; set; }

    // Assignment trio — these move together atomically (council M1 invariant, enforced in WP-04):
    // AssigneeUserId == null  ⟺  AssignmentKind == None  ⟺  ClaimedAt == null
    public int? AssigneeUserId { get; set; }
    public AssignmentKind AssignmentKind { get; set; } = AssignmentKind.None;
    public DateTime? ClaimedAt { get; set; }  // UTC

    public string? PhotoPath { get; set; }

    public DateTime CreatedAt { get; set; }  // UTC

    // Concurrency token (maps to PostgreSQL xmin) — council M7.
    [Timestamp]
    public uint Version { get; set; }

    // Navigation
    public Household Household { get; set; } = default!;
    public Room? Room { get; set; }
    public User EnteredBy { get; set; } = default!;
    public User? Owner { get; set; }
    public User? Assignee { get; set; }
    public ICollection<ChoreCompletion> Completions { get; set; } = new List<ChoreCompletion>();
    public ICollection<ChoreEvent> Events { get; set; } = new List<ChoreEvent>();
}
