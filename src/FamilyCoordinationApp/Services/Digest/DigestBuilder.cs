namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Pure, stateless builder that assembles a <see cref="DigestModel"/> from a per-household
/// snapshot. No I/O, no DI — safe to register as <c>AddSingleton&lt;DigestBuilder&gt;()</c>
/// without any captive-dependency risk (WP-06).
/// <para>
/// Framing contract (M11/M12/MN8): the headline is collective and non-punitive;
/// the distribution is ordered by name (stable, neutral) and carries no ranking label;
/// the FallingBehind list names chores, not people.
/// </para>
/// </summary>
public class DigestBuilder
{
    /// <summary>
    /// Build a <see cref="DigestModel"/> from the assembled household snapshot.
    /// </summary>
    /// <param name="householdName">Household display name (used for context; currently in headline).</param>
    /// <param name="equity">The equity result from <c>ChoreEquityCalculator.Compute</c>.</param>
    /// <param name="choreDueness">Per-chore dueness snapshot built by WP-05.</param>
    /// <param name="upForGrabsCount">Count of unclaimed chores open for anyone.</param>
    public DigestModel Build(
        string householdName,
        ChoreEquityResult equity,
        IReadOnlyList<DigestChoreLine> choreDueness,
        int upForGrabsCount)
    {
        var headline = BuildHeadline(householdName, equity.TotalCompletions, equity.TotalPoints);

        // Distribution: neutral alphabetical order (stable, not a ranking).
        var distribution = equity.Members
            .OrderBy(m => m.DisplayName, StringComparer.OrdinalIgnoreCase)
            .Select(m => new DigestMemberLine(m.DisplayName, m.Points, m.SharePct))
            .ToList();

        // FallingBehind: chores (not people) that need attention.
        var fallingBehind = choreDueness
            .Where(c => c.DueState is DueState.Overdue or DueState.DueToday)
            .Select(c => c.Name)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return new DigestModel(
            CollectiveHeadline: headline,
            TotalCompletions: equity.TotalCompletions,
            TotalPoints: equity.TotalPoints,
            Distribution: distribution,
            FallingBehind: fallingBehind,
            UpForGrabsCount: upForGrabsCount);
    }

    // ---- Headline (collective + non-punitive) -------------------------------------------------------

    private static string BuildHeadline(string householdName, int totalCompletions, int totalPoints) =>
        $"The {householdName} house knocked out {totalCompletions} chore{(totalCompletions == 1 ? "" : "s")} " +
        $"({totalPoints} pt{(totalPoints == 1 ? "" : "s")}) this week 💪";
}
