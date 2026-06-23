namespace FamilyCoordinationApp.Services.Dtos;

/// <summary>
/// The in-app weekly recap payload (read-only). The <see cref="Current"/> week mirrors EXACTLY what the
/// Discord digest posts (same headline / distribution / falling-behind / up-for-grabs — built from the same
/// <c>DigestBuilder</c>), so the app view and the channel post never diverge. <see cref="Trend"/> adds the
/// week-over-week history the digest can't show: per-week completion + point totals, oldest→newest.
/// <para>
/// Household-scoped (M1 — the household id comes from the resolved caller, never the client). No user ids and
/// no @mention fields anywhere (mirrors the digest's neutral framing).
/// </para>
/// </summary>
public sealed record ChoreRecapDto(
    RecapWeekDto Current,
    IReadOnlyList<RecapTrendPointDto> Trend);

/// <summary>
/// The current week's assembled recap — the same content the weekly Discord digest sends.
/// </summary>
/// <param name="WeekStartLocal">Local Monday of the week, ISO date string <c>yyyy-MM-dd</c> (computed
/// server-side in the household timezone — the island never does date math, MN9).</param>
/// <param name="Headline">Collective, non-punitive headline (e.g. "The … house knocked out 7 chores…").</param>
/// <param name="TotalCompletions">Chore completions in the week.</param>
/// <param name="TotalPoints">Effort points in the week.</param>
/// <param name="Distribution">Per-member effort split (neutral alphabetical order; no ranking, no user id).</param>
/// <param name="FallingBehind">Names of chores that are Overdue or DueToday (point-in-time attention list).</param>
/// <param name="UpForGrabsCount">Count of unclaimed chores open for anyone.</param>
public sealed record RecapWeekDto(
    string WeekStartLocal,
    string Headline,
    int TotalCompletions,
    int TotalPoints,
    IReadOnlyList<RecapMemberLineDto> Distribution,
    IReadOnlyList<string> FallingBehind,
    int UpForGrabsCount);

/// <summary>
/// One member's contribution line in the current-week recap. No user id / mention field — display name and
/// effort figures only (mirrors the digest's <c>DigestMemberLine</c>). <c>SharePct</c> is PERCENT 0–100.
/// </summary>
public sealed record RecapMemberLineDto(
    string DisplayName,
    int Points,
    double SharePct);

/// <summary>
/// One week's totals in the week-over-week trend. Totals only (no per-member split) — the trend answers
/// "how did the house's output move week to week", not "who did what N weeks ago".
/// </summary>
/// <param name="WeekStartLocal">Local Monday of the week, ISO date string <c>yyyy-MM-dd</c> (household tz).</param>
/// <param name="TotalCompletions">Chore completions in that week.</param>
/// <param name="TotalPoints">Effort points in that week.</param>
/// <param name="IsCurrent">True for the in-progress current week (the last, partial bar).</param>
public sealed record RecapTrendPointDto(
    string WeekStartLocal,
    int TotalCompletions,
    int TotalPoints,
    bool IsCurrent);
