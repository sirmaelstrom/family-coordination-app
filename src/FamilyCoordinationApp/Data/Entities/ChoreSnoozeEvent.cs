namespace FamilyCoordinationApp.Data.Entities;

/// <summary>
/// Append-only log of snooze-floor transitions for a chore (Phase 15 — the chore-history substrate). Each row
/// is ONE snooze action: a SET (<see cref="SnoozedUntil"/> non-null = the floor that was applied) or a CLEAR
/// (<see cref="SnoozedUntil"/> null = the floor was removed — an explicit un-snooze OR a satisfying completion,
/// see <c>ChoreService</c> D9). Rows are written only on an actual state CHANGE (a no-op re-snooze to the same
/// date, or a clear on an un-snoozed chore, writes nothing).
/// <para>
/// This ships now because snooze history is UNBACKFILLABLE — <see cref="Chore.SnoozedUntil"/> holds only the
/// CURRENT floor, so without this stream a past snooze (and whether a historical missed beat was <i>snoozed</i>
/// vs <i>slipped</i>) is unrecoverable. The read/UI use (richer "snoozed since March / re-snoozed 3×"
/// narration) is a deliberate fast-follow; the write-path exists now so the history accrues.
/// </para>
/// Sibling to <see cref="ChoreCompletion"/> / <see cref="ChoreEvent"/>: same composite-key + DB-generated-id
/// shape; a distinct entity (not a <see cref="ChoreEvent"/> type) because it carries the snooze-specific
/// <see cref="SnoozedUntil"/> date payload the generic audit event has no place for.
/// </summary>
public class ChoreSnoozeEvent
{
    public int HouseholdId { get; set; }
    public int ChoreId { get; set; }
    public int SnoozeEventId { get; set; }

    /// <summary>The user who performed the snooze action (for a completion-clear, the completer).</summary>
    public int SetByUserId { get; set; }

    /// <summary>UTC instant the action occurred.</summary>
    public DateTime SetAt { get; set; }

    /// <summary>
    /// The snooze floor that was applied (household-tz local date), or <c>null</c> when this event CLEARED the
    /// floor. Mirrors the semantics of <see cref="Chore.SnoozedUntil"/>.
    /// </summary>
    public DateOnly? SnoozedUntil { get; set; }

    // Navigation
    public Chore Chore { get; set; } = default!;
    public User SetBy { get; set; } = default!;
}
