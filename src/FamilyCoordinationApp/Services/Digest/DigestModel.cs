using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// The fully assembled digest payload for one household. Passed to <see cref="IDigestSender"/>
/// for rendering and delivery. No user IDs, no @mention fields (M11/MN8).
/// </summary>
public record DigestModel(
    /// <summary>Collective, non-punitive summary headline (e.g. "The house knocked out 7 chores…").</summary>
    string CollectiveHeadline,
    /// <summary>Total chore completions in the window.</summary>
    int TotalCompletions,
    /// <summary>Total effort points in the window.</summary>
    int TotalPoints,
    /// <summary>Per-member distribution (no ranking framing, no userId/mention field).</summary>
    IReadOnlyList<DigestMemberLine> Distribution,
    /// <summary>Names of chores that are Overdue or DueToday (attention list, not a blame list).</summary>
    IReadOnlyList<string> FallingBehind,
    /// <summary>Count of unclaimed chores open for anyone to grab.</summary>
    int UpForGrabsCount);

/// <summary>
/// A single member's contribution line in the digest distribution. Contains no userId or
/// mention field — only a display name and effort figures (M11/MN8).
/// </summary>
public record DigestMemberLine(
    string DisplayName,
    int Points,
    double SharePct);

/// <summary>
/// Input record carrying a chore's name + computed <see cref="DueState"/> for digest assembly.
/// WP-05 builds the list from the household's active chores via <see cref="ChoreStatusCalculator"/>.
/// </summary>
public record DigestChoreLine(string Name, DueState DueState);
