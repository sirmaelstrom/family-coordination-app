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
    double SharePct)
{
    /// <summary>
    /// The member's capacity-WEIGHTED fair share of the physical load (PERCENT 0–100, UNROUNDED —
    /// the UI formats). Phase 15 (D1/D3): a NON-positional <c>init</c>-only property in the record BODY,
    /// so every existing positional <c>new MemberEquityShare(7 args)</c> site (the calculator tests, the
    /// digest fixtures) compiles UNCHANGED. <c>SharePct</c> / <c>EqualSharePct</c> stay RAW physical facts
    /// (digest-safe); this is the per-member EXPECTED reference the island draws instead of the single flat
    /// equal-share line. Defaults to 0.0; the calculator sets the real computed value via object initializer.
    /// </summary>
    public double ExpectedSharePct { get; init; }
}

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
    /// <summary>Shared empty tier map for the no-capacity-data (all-<c>Full</c>) path.</summary>
    private static readonly IReadOnlyDictionary<int, string?> EmptyTiers =
        new Dictionary<int, string?>();

    /// <summary>
    /// Physical-capacity tier → expected-share weight (Phase 15 D3). <c>Full</c>=1.0, <c>Reduced</c>=0.5,
    /// <c>Minimal</c>=0.15 (NOT zero — keeps a Minimal member's reference humane and Σweight&gt;0). A
    /// null/absent/unrecognized tier is treated as <c>Full</c>. Tier strings mirror <c>CapacityTier.All</c>.
    /// </summary>
    private static double WeightFor(IReadOnlyDictionary<int, string?> tiers, int userId)
    {
        tiers.TryGetValue(userId, out var tier);
        return tier switch
        {
            "Reduced" => 0.5,
            "Minimal" => 0.15,
            _ => 1.0, // Full, null, or any unrecognized value
        };
    }

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
    /// <param name="tiersByUserId">Optional per-member physical-capacity tier map (<c>Full</c> /
    /// <c>Reduced</c> / <c>Minimal</c>, Phase 15 D3). A TRAILING OPTIONAL param (default <c>null</c>, the only
    /// compile-time-constant default C# allows) — an absent/empty map OR a null/unrecognized tier means that
    /// member is treated as <c>Full</c> (weight 1.0), so <c>ExpectedSharePct</c> falls back to the flat
    /// distribution. The digest call site omits this arg (all-<c>Full</c>) so its output stays byte-identical
    /// (MN2/D1). This does NOT touch <c>SharePct</c> / <c>EqualSharePct</c> — those stay RAW.</param>
    public ChoreEquityResult Compute(
        IEnumerable<ChoreCompletion> completions,
        IReadOnlyList<MemberDto> members,
        EquityWindow window,
        DateTime now,
        TimeZoneInfo tz,
        IReadOnlyDictionary<int, string?>? tiersByUserId = null)
    {
        var windowStart = ComputeWindowStart(window, now, tz);

        // Capacity weighting (D3): absent/empty map ⇒ every member Full (flat distribution). The default
        // MUST be a compile-time constant (null), so coalesce to an empty dictionary here.
        var tiers = tiersByUserId ?? EmptyTiers;

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

        // Capacity-weighted EXPECTED-share denominator (D3): Σ weight across members. Guard Σ==0
        // (impossible given Minimal=0.15>0, but explicit) by falling back to the flat 100/N distribution.
        double totalWeight = 0.0;
        foreach (var m in members)
        {
            totalWeight += WeightFor(tiers, m.UserId);
        }
        bool useFlatExpected = totalWeight <= 0.0;

        var shares = new List<MemberEquityShare>(memberCount);

        foreach (var m in members)
        {
            byUser.TryGetValue(m.UserId, out var userData);
            var memberPoints = userData.Points;
            var memberCompletions = userData.Completions;

            var sharePct = householdTotal == 0
                ? 0.0
                : Math.Round(100.0 * memberPoints / householdTotal, 1);

            // ExpectedSharePct (D3): 100 × weight[i] / Σweight, UNROUNDED (UI formats). The flat fallback
            // (Σweight==0) mirrors EqualSharePct's even split. Raw — does NOT touch SharePct/EqualSharePct.
            var expectedSharePct = memberCount == 0
                ? 0.0
                : useFlatExpected
                    ? 100.0 / memberCount
                    : 100.0 * WeightFor(tiers, m.UserId) / totalWeight;

            shares.Add(new MemberEquityShare(
                UserId: m.UserId,
                DisplayName: m.DisplayName,
                Initials: m.Initials,
                PictureUrl: m.PictureUrl,
                Points: memberPoints,
                Completions: memberCompletions,
                SharePct: sharePct)
            {
                ExpectedSharePct = expectedSharePct,
            });
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
