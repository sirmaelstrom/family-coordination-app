using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Pure progress-derivation for multi-person (co-sign) completion (D1=C, D3). The contributor count is
/// NEVER stored (M5/MN6) — it is derived on read the same way dueness is derived
/// (<see cref="ChoreStatusCalculator"/> philosophy): the distinct <c>CompletedByUserId</c> among
/// <see cref="ChoreCompletion"/> rows toward the CURRENT open occurrence (rows whose <c>CompletedAt</c> is
/// strictly after <c>LastCompletedAt</c>, or ALL rows when <c>LastCompletedAt is null</c>). Consumed by
/// <see cref="ChoreService"/> (write-path gate) and the board projection (WP-04, read-path "X of N").
/// </summary>
public static class ChoreCompletionProgress
{
    // Distinct user IDs who have contributed toward the CURRENT open occurrence:
    // completions with CompletedAt strictly after lastCompletedAt (or ALL when null).
    public static IReadOnlySet<int> DistinctContributorsSince(
        IEnumerable<ChoreCompletion> completions, DateTime? lastCompletedAt) =>
        completions
            .Where(c => lastCompletedAt is null || c.CompletedAt > lastCompletedAt.Value)
            .Select(c => c.CompletedByUserId)
            .ToHashSet();

    public static bool IsSatisfied(int distinctCount, int requiredCount) =>
        distinctCount >= Math.Max(1, requiredCount);
}
