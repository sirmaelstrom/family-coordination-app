using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Command to create a chore (council M11). The editable field subset; the service assigns identity,
/// authorship, timestamps, and the assignment trio. <paramref name="AssigneeUserId"/> non-null seeds the
/// chore as deliberately <see cref="AssignmentKind.Assigned"/> at creation; null leaves it on the pile.
/// </summary>
/// <param name="Name">Required, non-empty after trim.</param>
/// <param name="EffortTier">Named effort tier (P3) — drives the default <c>EffortPoints</c> snapshot.</param>
/// <param name="Icon">Optional emoji/short-code icon (parity with Room.Icon); empty string = none.</param>
/// <param name="AssignedUserIds">Initial named roster for a multi-person (<c>RequiredCount &gt; 1</c>) chore
/// (rework, D8) — one <c>Assigned</c> participation event is written per member at creation. Ignored for
/// X=1 chores (which use the single-holder trio via <see cref="AssigneeUserId"/>). null ⇒ no roster seed.</param>
public record CreateChoreCommand(
    string Name,
    string? Description,
    RecurrenceMode RecurrenceMode,
    int? IntervalDays,
    DateOnly? AnchorDate,
    ChoreDaysOfWeek? DaysOfWeek,
    int? DayOfMonth,
    EffortTier EffortTier,
    int? OwnerUserId,
    int? AssigneeUserId,
    string? PhotoPath,
    string Icon = "",
    int RequiredCount = 1,
    IReadOnlyList<int>? AssignedUserIds = null,
    // First-due floor at creation (null = due now). Persisted to Chore.SnoozedUntil; trailing + defaulted so
    // existing positional callers compile.
    DateOnly? SnoozedUntil = null,
    // Multi-room membership set (Phase 13) — the sole source of a chore's rooms (the Chore.RoomId shim was
    // dropped in WP-08). Trailing + defaulted so existing positional callers compile. On create, null/[] =
    // General (no memberships).
    IReadOnlyList<int>? RoomIds = null);

/// <summary>
/// Command to update a chore's editable fields (council M11) — the same editable subset as
/// <see cref="CreateChoreCommand"/>, minus assignment (assignment moves via claim/drop/hand-off, never an
/// edit). The assignment trio is NOT touched by an update.
/// </summary>
public record UpdateChoreCommand(
    string Name,
    string? Description,
    RecurrenceMode RecurrenceMode,
    int? IntervalDays,
    DateOnly? AnchorDate,
    ChoreDaysOfWeek? DaysOfWeek,
    int? DayOfMonth,
    EffortTier EffortTier,
    int? OwnerUserId,
    string? PhotoPath,
    string Icon = "",
    int RequiredCount = 1,
    // Next-due floor from the edit sheet (null = no floor). Persisted to Chore.SnoozedUntil; trailing +
    // defaulted so existing positional callers compile.
    DateOnly? SnoozedUntil = null,
    // Multi-room membership set (Phase 13) — the sole source of a chore's rooms (the Chore.RoomId shim was
    // dropped in WP-08). Trailing + defaulted so existing positional callers compile. On update, null =
    // PRESERVE existing memberships (no-op); [] clears to General.
    IReadOnlyList<int>? RoomIds = null);
