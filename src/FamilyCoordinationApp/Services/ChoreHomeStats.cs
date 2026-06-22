using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Pure, unit-testable reducer for the Home page's chore summary counts (WP-04), extracted from the private
/// <c>Home.razor</c> <c>LoadChoreStats</c> method so the count logic — specifically the snooze guard on
/// up-for-grabs — can be tested without a bUnit/Razor harness (the test project has none). Every count derives
/// from the SERVER-computed <see cref="ChoreDto"/> fields (<c>dueState</c>/<c>assignmentKind</c>/
/// <c>isClaimStale</c>/<c>isSnoozed</c>) — NO client-side date math (M5/MN4).
/// </summary>
internal static class ChoreHomeStats
{
    /// <summary>The four Home chore-card counts.</summary>
    internal sealed record Result(int Total, int Overdue, int DueToday, int UpForGrabs);

    /// <summary>
    /// Reduce the board chores into the Home counts. A snoozed chore already reads <c>Scheduled</c>, so it is
    /// auto-excluded from <c>Overdue</c>/<c>DueToday</c>; only <c>UpForGrabs</c> (the assignment-keyed bucket)
    /// needs the explicit <c>!IsSnoozed</c> guard, or a snoozed unclaimed chore would still surface.
    /// </summary>
    internal static Result Compute(IReadOnlyList<ChoreDto> chores) => new(
        Total: chores.Count,
        Overdue: chores.Count(c => c.DueState == DueState.Overdue),
        DueToday: chores.Count(c => c.DueState == DueState.DueToday),
        UpForGrabs: chores.Count(c => !c.IsSnoozed && (c.AssignmentKind == AssignmentKind.None || c.IsClaimStale)));
}
