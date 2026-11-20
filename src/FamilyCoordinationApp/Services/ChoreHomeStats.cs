using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Pure, unit-testable reducer for the Home page's chore summary counts (WP-04), extracted from the private
/// <c>Home.razor</c> <c>LoadChoreStats</c> method so the count logic — specifically the snooze guard on
/// up-for-grabs — can be tested without a bUnit/Razor harness (the test project has none). Every count derives
/// from the SERVER-computed <see cref="ChoreDto"/> fields (<c>dueState</c>/<c>assignmentKind</c>/
/// <c>isClaimStale</c>/<c>isSnoozed</c>/<c>roster</c>) — NO client-side date math (M5/MN4).
/// </summary>
internal static class ChoreHomeStats
{
    /// <summary>The four Home chore-card counts.</summary>
    internal sealed record Result(int Total, int Overdue, int DueToday, int UpForGrabs);

    /// <summary>
    /// Reduce the board chores into the Home counts as <paramref name="userId"/> sees them. A snoozed chore
    /// already reads <c>Scheduled</c>, so it is auto-excluded from <c>Overdue</c>/<c>DueToday</c>;
    /// <c>UpForGrabs</c> applies <see cref="IsUpForGrabs"/>, the board lens's rule.
    /// </summary>
    internal static Result Compute(IReadOnlyList<ChoreDto> chores, int userId) => new(
        Total: chores.Count,
        Overdue: chores.Count(c => c.DueState == DueState.Overdue),
        DueToday: chores.Count(c => c.DueState == DueState.DueToday),
        UpForGrabs: chores.Count(c => IsUpForGrabs(
            c.IsSnoozed, c.RequiredCount, c.CompletedCount, c.Roster.Select(m => m.UserId),
            c.AssignmentKind, c.IsClaimStale, userId)));

    /// <summary>
    /// Whether a chore is up for grabs to <paramref name="userId"/>: the C# copy of the board's "Up for grabs"
    /// lens (<c>upForGrabsChores</c>, <c>frontend/app/src/lib/chores/lib/state.svelte.ts</c>), so Home's count
    /// and the lens agree member by member. A snoozed chore never is. An unfinished multi-person chore
    /// (<paramref name="requiredCount"/> &gt; 1, fewer than that many done) is, to every member not on its
    /// roster in any state. Any other chore is when nobody has it or its claim is stale.
    /// </summary>
    internal static bool IsUpForGrabs(
        bool isSnoozed,
        int requiredCount,
        int completedCount,
        IEnumerable<int> rosterUserIds,
        AssignmentKind assignmentKind,
        bool isClaimStale,
        int userId)
    {
        if (isSnoozed)
        {
            return false;
        }

        if (requiredCount > 1 && completedCount < requiredCount)
        {
            return !rosterUserIds.Contains(userId);
        }

        return assignmentKind == AssignmentKind.None || isClaimStale;
    }
}
