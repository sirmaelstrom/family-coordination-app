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
public record CreateChoreCommand(
    string Name,
    string? Description,
    int? RoomId,
    RecurrenceMode RecurrenceMode,
    int? IntervalDays,
    DateOnly? AnchorDate,
    ChoreDaysOfWeek? DaysOfWeek,
    int? DayOfMonth,
    EffortTier EffortTier,
    int? OwnerUserId,
    int? AssigneeUserId,
    string? PhotoPath,
    string Icon = "");

/// <summary>
/// Command to update a chore's editable fields (council M11) — the same editable subset as
/// <see cref="CreateChoreCommand"/>, minus assignment (assignment moves via claim/drop/hand-off, never an
/// edit). The assignment trio is NOT touched by an update.
/// </summary>
public record UpdateChoreCommand(
    string Name,
    string? Description,
    int? RoomId,
    RecurrenceMode RecurrenceMode,
    int? IntervalDays,
    DateOnly? AnchorDate,
    ChoreDaysOfWeek? DaysOfWeek,
    int? DayOfMonth,
    EffortTier EffortTier,
    int? OwnerUserId,
    string? PhotoPath,
    string Icon = "");
