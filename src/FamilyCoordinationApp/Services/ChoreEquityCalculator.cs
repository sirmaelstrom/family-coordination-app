using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// The frozen result type for equity computation (council C1 — restated verbatim in WP-04 and WP-06
/// preconditions). <c>EqualSharePct</c> and per-member <c>SharePct</c> are PERCENT 0–100 (council M5/M6).
/// </summary>
public record ChoreEquityResult(
    int TotalPoints,
    int TotalCompletions,
    double EqualSharePct,
    IReadOnlyList<MemberEquityShare> Members);

/// <summary>
/// Per-member equity share in the computation result. <c>SharePct</c> is PERCENT 0–100.
/// </summary>
public record MemberEquityShare(
    int UserId,
    string DisplayName,
    string Initials,
    string? PictureUrl,
    int Points,
    int Completions,
    double SharePct);

/// <summary>
/// Pure, stateless, parameterless-ctor calculator that aggregates the completion log into a
/// per-member effort-weighted equity distribution over a window (v1.1 WP-02).
/// <para>
/// Takes an already-fetched <paramref name="completions"/> enumerable, an injected <c>now</c> (UTC),
/// and an injected <see cref="TimeZoneInfo"/>. ALL day-boundary math is done in the injected
/// timezone's local calendar — there is no <see cref="DateTime.UtcNow"/> /
/// <see cref="DateTime.Now"/> reached for inside (council M5).
/// </para>
/// <para>
/// <c>Week</c> window starts <b>Monday</b>, mirroring the <c>MealPlanService.GetWeekStartDate</c>
/// convention: <c>daysFromMonday = (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7</c>. This pairs
/// with the Sunday-18:00 digest send so the digest reports the full Mon–Sun week.
/// </para>
/// <para>
/// Members with zero completions in the window appear with 0 points / 0 completions / 0 SharePct
/// (not omitted). Empty household (zero members) returns a zero result without divide-by-zero.
/// </para>
/// </summary>
public class ChoreEquityCalculator
{
    /// <summary>
    /// Compute the equity distribution for <paramref name="members"/> over the given
    /// <paramref name="window"/> using the effort-weighted <paramref name="completions"/> log.
    /// </summary>
    /// <param name="completions">All relevant completions for the household (already fetched; the
    /// caller is responsible for HouseholdId scoping — M1).</param>
    /// <param name="members">The household member list. All members appear in the result,
    /// including those with zero activity in the window.</param>
    /// <param name="window">The aggregation window. <see cref="EquityWindow.Week"/> = current
    /// Mon–Sun local week; <see cref="EquityWindow.All"/> = no lower bound.</param>
    /// <param name="now">The current UTC instant (injected — never <see cref="DateTime.UtcNow"/>
    /// inside this method).</param>
    /// <param name="tz">The household timezone for local-day boundary math (M5).</param>
    public ChoreEquityResult Compute(
        IEnumerable<ChoreCompletion> completions,
        IReadOnlyList<MemberDto> members,
        EquityWindow window,
        DateTime now,
        TimeZoneInfo tz)
    {
        var windowStart = ComputeWindowStart(window, now, tz);

        // Aggregate effort points + completion count per user for in-window completions.
        var byUser = new Dictionary<int, (int Points, int Completions)>();
        int householdTotal = 0;
        int householdCompletions = 0;

        foreach (var c in completions)
        {
            // All timestamps on ChoreCompletion are UTC (entity doc).
            var completedAt = DateTime.SpecifyKind(c.CompletedAt, DateTimeKind.Utc);

            if (windowStart is { } start && completedAt < start)
            {
                continue;
            }

            byUser.TryGetValue(c.CompletedByUserId, out var existing);
            byUser[c.CompletedByUserId] = (
                existing.Points + c.EffortPointsSnapshot,
                existing.Completions + 1);

            householdTotal += c.EffortPointsSnapshot;
            householdCompletions++;
        }

        int memberCount = members.Count;

        // EqualSharePct: what each member's fair share would be (0 when no members).
        var equalSharePct = memberCount == 0 ? 0.0 : Math.Round(100.0 / memberCount, 1);

        var shares = new List<MemberEquityShare>(memberCount);

        foreach (var m in members)
        {
            byUser.TryGetValue(m.UserId, out var userData);
            var memberPoints = userData.Points;
            var memberCompletions = userData.Completions;

            var sharePct = householdTotal == 0
                ? 0.0
                : Math.Round(100.0 * memberPoints / householdTotal, 1);

            shares.Add(new MemberEquityShare(
                UserId: m.UserId,
                DisplayName: m.DisplayName,
                Initials: m.Initials,
                PictureUrl: m.PictureUrl,
                Points: memberPoints,
                Completions: memberCompletions,
                SharePct: sharePct));
        }

        return new ChoreEquityResult(
            TotalPoints: householdTotal,
            TotalCompletions: householdCompletions,
            EqualSharePct: equalSharePct,
            Members: shares);
    }

    // ---- Window boundary (Monday-start, all math in local tz) ----------------------------------------

    /// <summary>
    /// Returns the inclusive UTC lower-bound for the window, or <c>null</c> for
    /// <see cref="EquityWindow.All"/>. The week lower-bound is the local Monday 00:00 of the week
    /// containing <paramref name="now"/>, converted back to UTC (mirrors
    /// <c>MealPlanService.GetWeekStartDate</c>).
    /// </summary>
    private static DateTime? ComputeWindowStart(EquityWindow window, DateTime now, TimeZoneInfo tz)
    {
        if (window == EquityWindow.All)
        {
            return null;
        }

        // Convert now to local calendar date in the injected timezone.
        var asUtc = DateTime.SpecifyKind(now, DateTimeKind.Utc);
        var localNow = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);
        var localDate = DateOnly.FromDateTime(localNow);

        // Mirror MealPlanService.GetWeekStartDate: (7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7
        var daysFromMonday = (7 + (localDate.DayOfWeek - DayOfWeek.Monday)) % 7;
        var mondayDate = localDate.AddDays(-daysFromMonday);

        // Local-midnight of Monday in the injected tz, back to UTC.
        var mondayMidnightLocal = mondayDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(mondayMidnightLocal, tz);
    }
}
