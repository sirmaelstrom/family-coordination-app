using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Canonical chore-attention predicates shared by WP-05 (the weekly digest) and WP-06 (the equity
/// endpoint) so the lens and the digest tell the same story (council MAJOR — these were previously
/// computed in two places and could diverge). Pure, dependency-free, fully unit-testable.
/// <list type="bullet">
///   <item><description><see cref="IsFallingBehind"/> — a chore carrying dueness pressure (overdue
///   or due today). Names a CHORE, not a person (M11/MN8).</description></item>
///   <item><description><see cref="IsUpForGrabs"/> — a chore anyone can grab: either unassigned, or a
///   self-claim that has gone stale (the caller supplies the staleness verdict from
///   <see cref="ChoreStatusCalculator.IsClaimStale"/>).</description></item>
/// </list>
/// </summary>
internal static class ChoreAttention
{
    /// <summary>True when a chore carries dueness pressure (Overdue or DueToday).</summary>
    public static bool IsFallingBehind(DueState d) => d is DueState.Overdue or DueState.DueToday;

    /// <summary>
    /// True when a chore is open for anyone to grab: unassigned (<see cref="AssignmentKind.None"/>) or a
    /// stale self-claim (<paramref name="isClaimStale"/>, computed by the caller via
    /// <see cref="ChoreStatusCalculator.IsClaimStale"/>).
    /// </summary>
    public static bool IsUpForGrabs(AssignmentKind kind, bool isClaimStale)
        => kind == AssignmentKind.None || isClaimStale;
}
