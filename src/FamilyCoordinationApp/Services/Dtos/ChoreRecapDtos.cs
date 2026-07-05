namespace FamilyCoordinationApp.Services.Dtos;

/// <summary>
/// The in-app weekly recap payload — evolved into the logbook (A) for the chore-history surface (Phase 15).
/// The <see cref="Current"/> week mirrors EXACTLY what the Discord digest posts (same headline / distribution /
/// falling-behind / up-for-grabs — built from the same <c>DigestBuilder</c>), so the app view and the channel
/// post never diverge (M6 — byte-identical, contract-tested). <see cref="Trend"/> adds the week-over-week
/// history; each point now also carries a per-week <see cref="RecapTrendPointDto.Distribution"/>. The Phase-15
/// additions — <see cref="Milestones"/>, <see cref="KeptMoments"/>, <see cref="WhatGotTended"/>, and
/// <see cref="GoneQuiet"/> (the shared band, same data as the ledger) — are ADDITIVE sibling fields projected
/// from the shared <c>ChoreHistoryResult</c> (D2/D3); existing consumers ignore them.
/// <para>
/// Household-scoped (M1 — the household id comes from the resolved caller, never the client). No user ids and
/// no @mention fields anywhere (mirrors the digest's neutral framing, D6/MN1).
/// </para>
/// </summary>
public sealed record ChoreRecapDto(
    RecapWeekDto Current,
    IReadOnlyList<RecapTrendPointDto> Trend,
    MilestonesDto Milestones,
    IReadOnlyList<KeptMomentDto> KeptMoments,
    IReadOnlyList<WhatGotTendedDto> WhatGotTended,
    IReadOnlyList<GoneQuietDto> GoneQuiet);

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
/// One week's totals in the week-over-week trend, plus its per-member <see cref="Distribution"/> (Phase 15,
/// D6). The distribution is a WITHIN-week breakdown (displayName-only, no userId) rendered on a selected week —
/// it is NOT a per-person series across weeks (MN2: that would rebuild the dropped "B" scoreboard).
/// </summary>
/// <param name="WeekStartLocal">Local Monday of the week, ISO date string <c>yyyy-MM-dd</c> (household tz).</param>
/// <param name="TotalCompletions">Chore completions in that week.</param>
/// <param name="TotalPoints">Effort points in that week.</param>
/// <param name="IsCurrent">True for the in-progress current week (the last, partial bar).</param>
/// <param name="Distribution">Per-member effort split for THIS week (displayName only; sums to the week total).</param>
public sealed record RecapTrendPointDto(
    string WeekStartLocal,
    int TotalCompletions,
    int TotalPoints,
    bool IsCurrent,
    IReadOnlyList<RecapMemberLineDto> Distribution);

/// <summary>
/// The collective milestones over the recap window (Phase 15 — the logbook's "moments" strip). All effort/
/// count facts, no per-person ranking.
/// </summary>
/// <param name="BestWeek">The highest-output week in the window (<c>null</c> when the window had no activity).</param>
/// <param name="LongestActiveStreakWeeks">Longest run of consecutive non-empty weeks.</param>
/// <param name="FirstEvers">Chores whose ALL-TIME first completion landed in the window ("first time!").</param>
/// <param name="SeasonTotalCompletions">Total completions over the window.</param>
/// <param name="SeasonTotalPoints">Total effort points over the window.</param>
public sealed record MilestonesDto(
    BestWeekDto? BestWeek,
    int LongestActiveStreakWeeks,
    IReadOnlyList<FirstEverDto> FirstEvers,
    int SeasonTotalCompletions,
    int SeasonTotalPoints);

/// <summary>
/// The highest-output week. A DEDICATED record (NOT <see cref="RecapTrendPointDto"/>, which now carries a
/// distribution): it uses <c>TotalCompletions</c>/<c>TotalPoints</c> to match the sibling recap wire DTOs.
/// </summary>
public sealed record BestWeekDto(string WeekStartLocal, int TotalCompletions, int TotalPoints);

/// <summary>A chore whose all-time first-ever completion landed in the window.</summary>
public sealed record FirstEverDto(string ChoreName, string LocalDate);

/// <summary>A completion that carried a note or a photo — the logbook's "kept moments" highlight (newest-first, cap 12).</summary>
/// <param name="LocalDate">Household-local completion date, <c>yyyy-MM-dd</c> (MN9).</param>
public sealed record KeptMomentDto(string LocalDate, string ChoreName, string? Note, bool HasPhoto);

/// <summary>Per-room completion tally over the window. Roomless completions bucket into the virtual "General" group.</summary>
public sealed record WhatGotTendedDto(string RoomName, int Completions);
