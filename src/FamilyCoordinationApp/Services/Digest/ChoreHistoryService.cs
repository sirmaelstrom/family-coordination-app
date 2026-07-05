using System.Globalization;
using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Services.Digest;

/// <summary>
/// Read-only aggregation service behind the chore-history surface (Phase 15). It computes the collective,
/// non-punitive history primitives over a bounded window — the completion feed, weekly aggregates + per-week
/// distribution, the historical gap/ghost projection, the gone-quiet band, and milestones — and returns them
/// in ONE explicit internal result record (<see cref="ChoreHistoryResult"/>). The ledger endpoint (WP-04) and
/// the evolved recap (WP-05) each project a subset of that result; both surfaces read the SAME computation so
/// a gone-quiet chore or a per-week share can never diverge between them (D3).
/// <para>
/// Construction + read idiom mirror <see cref="ChoreRecapService"/> exactly: a short-lived
/// <see cref="IDbContextFactory{TContext}"/> context per call (M2), <c>AsNoTracking</c>, every query scoped to
/// <c>householdId</c> from the resolved caller (M1 — a security boundary), and completions bounded to the
/// oldest trend week (<c>CompletedAt &gt;= oldestWeekStartUtc</c>) so a long-lived household reads O(weeks),
/// never O(all history). Two bounded exceptions push aggregation to Postgres instead of loading all rows: the
/// first-ever detection runs a single <c>MIN(CompletedAt) GROUP BY ChoreId</c> (O(chores), D10), and WP-03's
/// snooze fold seeds pre-window state per chore.
/// </para>
/// <para>
/// This service must NOT depend on <see cref="IChoreRecapService"/>/<see cref="ChoreRecapService"/> — the
/// dependency is one-way (WP-05 makes recap depend on history), so any shared code lives in pure static
/// helpers (<see cref="ChoreEquityCalculator.WeekStartUtc"/>, <see cref="ChoreStatusCalculator.LocalDate"/>
/// et al.), never a back-reference.
/// </para>
/// </summary>
public interface IChoreHistoryService
{
    /// <summary>
    /// Compute the full history result for <paramref name="householdId"/> over the trailing
    /// <paramref name="weeks"/> weeks (clamped 1–26), oldest→newest and INCLUDING empty weeks.
    /// </summary>
    /// <param name="householdId">Resolved caller's household (M1 — never a client-supplied id).</param>
    /// <param name="weeks">How many weeks of history to window (including the current week). Clamped 1–26.</param>
    /// <param name="now">UTC instant to evaluate against; defaults to the injected <c>TimeProvider</c> now.</param>
    /// <param name="ct">Cancellation token.</param>
    Task<ChoreHistoryResult> GetHistoryAsync(int householdId, int weeks, DateTime? now = null, CancellationToken ct = default);
}

// ── The frozen cross-wave contract (internal records, NOT wire DTOs) ─────────────────────────────────
// WP-01 populates WindowStart/EndLocal, Events, Weeks, Milestones, KeptMoments, WhatGotTended.
// WP-02 fills Ghosts + GoneQuiet (empty here). WP-03 overwrites their Reason from the snooze log.
// WP-04/05 project subsets of this shape onto the wire DTOs. Do not reshape without re-freezing downstream.

/// <summary>
/// The complete history computation for a household over a window. <c>WindowStartLocal</c>/<c>WindowEndLocal</c>
/// are <c>yyyy-MM-dd</c> household-tz strings (MN9). <c>Weeks</c> is oldest→newest and includes empty weeks.
/// </summary>
public sealed record ChoreHistoryResult(
    string WindowStartLocal, string WindowEndLocal,
    IReadOnlyList<HistoryEvent> Events,
    IReadOnlyList<HistoryWeek> Weeks,
    IReadOnlyList<HistoryGhost> Ghosts,          // EMPTY from WP-01 (WP-02 fills)
    IReadOnlyList<HistoryGoneQuiet> GoneQuiet,   // EMPTY from WP-01 (WP-02/03 fill)
    HistoryMilestones Milestones,
    IReadOnlyList<HistoryKeptMoment> KeptMoments,
    IReadOnlyList<HistoryRoomTally> WhatGotTended);

/// <summary>One completion in the feed. displayName-only (no userId) — neutral framing (D9/MN1).</summary>
public sealed record HistoryEvent(string ChoreName, string DoerDisplayName, string LocalDate, int Points, string? Note, bool HasPhoto);

/// <summary>One week's totals + per-member distribution (displayName/points/sharePct — no userId).</summary>
public sealed record HistoryWeek(string WeekStartLocal, int Completions, int Points, IReadOnlyList<RecapMemberLineDto> Distribution);

/// <summary>An expected-but-unmatched beat. <c>ReasonFromLog</c> is server-internal telemetry (D5), off-wire.
/// WP-02 sets a current-state Reason + ReasonFromLog=false; WP-03 overwrites both with the log-derived value.</summary>
public sealed record HistoryGhost(string ChoreName, string ExpectedLocalDate, string Reason, bool ReasonFromLog);

/// <summary>A chore with a trailing run of misses (the gone-quiet band). <c>LastCompletedLocalDate</c> NULL when never completed.</summary>
public sealed record HistoryGoneQuiet(string ChoreName, string CadenceLabel, string? LastCompletedLocalDate, string Reason);

/// <summary>The collective milestones over the window.</summary>
public sealed record HistoryMilestones(BestWeek? BestWeek, int LongestActiveStreakWeeks, IReadOnlyList<FirstEver> FirstEvers, int SeasonTotalCompletions, int SeasonTotalPoints);

/// <summary>The highest-output week in the window. Dedicated record — do NOT reuse a trend/week DTO.</summary>
public sealed record BestWeek(string WeekStartLocal, int Completions, int Points);

/// <summary>A chore whose all-time first-ever completion landed in the window.</summary>
public sealed record FirstEver(string ChoreName, string LocalDate);

/// <summary>A completion that carried a note or a photo (the "kept moments" highlight reel).</summary>
public sealed record HistoryKeptMoment(string LocalDate, string ChoreName, string? Note, bool HasPhoto);

/// <summary>Per-room completion tally over the window. Roomless completions bucket into the virtual "General" group.</summary>
public sealed record HistoryRoomTally(string RoomName, int Completions);

/// <inheritdoc cref="IChoreHistoryService"/>
public class ChoreHistoryService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ChoreEquityCalculator equity,
    ChoreStatusCalculator status,
    TimeZoneInfo tz,
    TimeProvider timeProvider) : IChoreHistoryService
{
    private const int MinWeeks = 1;
    private const int MaxWeeks = 26;

    /// <summary>Fallback display name for a completion whose completer is no longer a household member.</summary>
    private const string UnknownDoerName = "A household member";

    /// <summary>The "kept moments" highlight reel is capped so the payload stays small (newest-first).</summary>
    private const int KeptMomentsCap = 12;

    /// <inheritdoc />
    public async Task<ChoreHistoryResult> GetHistoryAsync(
        int householdId, int weeks, DateTime? now = null, CancellationToken ct = default)
    {
        var asOf = DateTime.SpecifyKind(now ?? timeProvider.GetUtcNow().UtcDateTime, DateTimeKind.Utc);
        weeks = Math.Clamp(weeks, MinWeeks, MaxWeeks);

        // Window lower bound = the local Monday of the oldest trend week (mirrors ChoreRecapService). All reads
        // are bounded to this so the scan is O(weeks of data), not O(all history) — M2.
        var oldestWeekStartUtc = ChoreEquityCalculator.WeekStartUtc(asOf.AddDays(-7 * (weeks - 1)), tz);

        await using var context = await dbFactory.CreateDbContextAsync(ct);

        // Members — HouseholdId-scoped, alphabetical (M1), exactly as ChoreRecapService reads them.
        var members = await context.Users
            .AsNoTracking()
            .Where(u => u.HouseholdId == householdId)
            .OrderBy(u => u.DisplayName)
            .Select(u => new MemberDto(u.Id, u.DisplayName, u.Initials, u.PictureUrl))
            .ToListAsync(ct);

        // In-window completions — the feed/weeks/distribution substrate. Window-bounded (M2), AsNoTracking.
        var completions = await context.ChoreCompletions
            .AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.CompletedAt >= oldestWeekStartUtc)
            .ToListAsync(ct);

        // Chore name/room lookup for ALL household chores (O(chores)). An in-window completion always has a live
        // chore row (delete cascades/restricts completions), so every referenced ChoreId resolves.
        var choreLookup = await context.Chores
            .AsNoTracking()
            .Where(c => c.HouseholdId == householdId)
            .Select(c => new { c.ChoreId, c.Name, c.RoomId })
            .ToListAsync(ct);
        var choreById = choreLookup.ToDictionary(c => c.ChoreId, c => (c.Name, c.RoomId));

        // Room names for the what-got-tended tally (O(rooms)); roomless → the virtual "General" group.
        var roomNameById = await context.Rooms
            .AsNoTracking()
            .Where(r => r.HouseholdId == householdId)
            .ToDictionaryAsync(r => r.RoomId, r => r.Name, ct);

        // First-ever detection (D10): a bounded DB-side MIN(CompletedAt) GROUP BY ChoreId — one row per chore
        // (O(chores), indexed on HouseholdId+CompletedAt), NOT a full-history scan. A chore's first-ever is
        // in-window iff its all-time earliest completion falls in [oldestWeekStartUtc, asOf]. Materialize the
        // anonymous projection into concrete tuples so it crosses into the pure helper cleanly.
        var firstEverRows = await context.ChoreCompletions
            .AsNoTracking()
            .Where(c => c.HouseholdId == householdId)
            .GroupBy(c => c.ChoreId)
            .Select(g => new { ChoreId = g.Key, FirstCompletedAt = g.Min(c => c.CompletedAt) })
            .ToListAsync(ct);
        var firstEverByChore = firstEverRows
            .Select(r => (r.ChoreId, r.FirstCompletedAt))
            .ToList();

        var memberNameById = members.ToDictionary(m => m.UserId, m => m.DisplayName);

        var events = BuildEvents(completions, choreById, memberNameById);
        var weekBuckets = BucketByWeek(completions, asOf, weeks);
        var weeksResult = BuildWeeks(weekBuckets, members, asOf);
        var milestones = BuildMilestones(weeksResult, firstEverByChore, choreById, oldestWeekStartUtc, asOf);
        var keptMoments = BuildKeptMoments(completions, choreById);
        var whatGotTended = BuildWhatGotTended(completions, choreById, roomNameById);

        return new ChoreHistoryResult(
            WindowStartLocal: LocalIsoDate(oldestWeekStartUtc),
            WindowEndLocal: LocalIsoDate(asOf),
            Events: events,
            Weeks: weeksResult,
            Ghosts: Array.Empty<HistoryGhost>(),          // WP-02 fills
            GoneQuiet: Array.Empty<HistoryGoneQuiet>(),    // WP-02/03 fill
            Milestones: milestones,
            KeptMoments: keptMoments,
            WhatGotTended: whatGotTended);
    }

    // ── Events — the completion feed (newest-first) ──────────────────────────────────────────────────

    private IReadOnlyList<HistoryEvent> BuildEvents(
        IReadOnlyList<ChoreCompletion> completions,
        IReadOnlyDictionary<int, (string Name, int? RoomId)> choreById,
        IReadOnlyDictionary<int, string> memberNameById)
    {
        return completions
            .OrderByDescending(c => c.CompletedAt)
            .Select(c => new HistoryEvent(
                ChoreName: choreById.TryGetValue(c.ChoreId, out var chore) ? chore.Name : string.Empty,
                DoerDisplayName: memberNameById.TryGetValue(c.CompletedByUserId, out var name) ? name : UnknownDoerName,
                LocalDate: LocalIsoDate(c.CompletedAt),
                Points: c.EffortPointsSnapshot,
                Note: string.IsNullOrWhiteSpace(c.Note) ? null : c.Note,
                HasPhoto: !string.IsNullOrWhiteSpace(c.PhotoPath)))
            .ToList();
    }

    // ── Weekly aggregates + per-week distribution ────────────────────────────────────────────────────

    /// <summary>
    /// Bucket in-window completions by their local week-start (the SAME <see cref="ChoreEquityCalculator.WeekStartUtc"/>
    /// key the recap trend uses, so the two never diverge — WP-01 test (a)), then emit one bucket per requested
    /// week oldest→newest INCLUDING empty weeks (k = weeks−1 … 0; k = 0 is the in-progress current week).
    /// </summary>
    private (DateTime WeekStartUtc, List<ChoreCompletion> Completions)[] BucketByWeek(
        IReadOnlyList<ChoreCompletion> completions, DateTime now, int weeks)
    {
        var byWeekStart = new Dictionary<DateTime, List<ChoreCompletion>>();
        foreach (var c in completions)
        {
            var at = DateTime.SpecifyKind(c.CompletedAt, DateTimeKind.Utc);
            var weekStart = ChoreEquityCalculator.WeekStartUtc(at, tz);
            if (!byWeekStart.TryGetValue(weekStart, out var bucket))
            {
                bucket = new List<ChoreCompletion>();
                byWeekStart[weekStart] = bucket;
            }
            bucket.Add(c);
        }

        var result = new (DateTime, List<ChoreCompletion>)[weeks];
        // Oldest → newest; index 0 is the oldest week, the last index (k == 0) is the current week.
        for (var k = weeks - 1; k >= 0; k--)
        {
            var weekStartUtc = ChoreEquityCalculator.WeekStartUtc(now.AddDays(-7 * k), tz);
            byWeekStart.TryGetValue(weekStartUtc, out var bucket);
            result[weeks - 1 - k] = (weekStartUtc, bucket ?? new List<ChoreCompletion>());
        }
        return result;
    }

    private IReadOnlyList<HistoryWeek> BuildWeeks(
        (DateTime WeekStartUtc, List<ChoreCompletion> Completions)[] buckets,
        IReadOnlyList<MemberDto> members,
        DateTime now)
    {
        var weeks = new List<HistoryWeek>(buckets.Length);
        foreach (var (weekStartUtc, bucket) in buckets)
        {
            var points = bucket.Sum(c => c.EffortPointsSnapshot);

            // Per-week distribution: compute equity over JUST this week's bucket with EquityWindow.All (no lower
            // bound — the bucket is already the week), so a member's per-week share sums to the week total. NOT
            // EquityWindow.Week, whose lower-bound-only window would cumulatively over-count every week but the
            // last. displayName-only (D6/MN1).
            var equityResult = equity.Compute(bucket, members, EquityWindow.All, now, tz);
            var distribution = equityResult.Members
                .Select(m => new RecapMemberLineDto(m.DisplayName, m.Points, m.SharePct))
                .ToList();

            weeks.Add(new HistoryWeek(
                WeekStartLocal: LocalIsoDate(weekStartUtc),
                Completions: bucket.Count,
                Points: points,
                Distribution: distribution));
        }
        return weeks;
    }

    // ── Milestones — best week, streak, first-evers, season totals ───────────────────────────────────

    private HistoryMilestones BuildMilestones(
        IReadOnlyList<HistoryWeek> weeks,
        IReadOnlyList<(int ChoreId, DateTime FirstCompletedAt)> firstEverByChore,
        IReadOnlyDictionary<int, (string Name, int? RoomId)> choreById,
        DateTime oldestWeekStartUtc,
        DateTime asOf)
    {
        // Best week: rank by Points, tie-break Completions. Null when the window had no completions at all.
        BestWeek? bestWeek = null;
        foreach (var w in weeks)
        {
            if (w.Completions == 0)
            {
                continue;
            }
            if (bestWeek is null
                || w.Points > bestWeek.Points
                || (w.Points == bestWeek.Points && w.Completions > bestWeek.Completions))
            {
                bestWeek = new BestWeek(w.WeekStartLocal, w.Completions, w.Points);
            }
        }

        // Longest run of consecutive non-empty weeks (oldest→newest order is already how weeks is built).
        var longestStreak = 0;
        var run = 0;
        foreach (var w in weeks)
        {
            run = w.Completions > 0 ? run + 1 : 0;
            longestStreak = Math.Max(longestStreak, run);
        }

        // First-evers: a chore whose all-time earliest completion (the bounded MIN aggregation) falls in the
        // window. A chore completed BEFORE oldestWeekStartUtc is NOT a first-ever, even if it recurs in-window.
        var firstEvers = new List<FirstEver>();
        foreach (var (choreId, firstCompletedAt) in firstEverByChore)
        {
            var first = DateTime.SpecifyKind(firstCompletedAt, DateTimeKind.Utc);
            if (first < oldestWeekStartUtc || first > asOf)
            {
                continue;
            }
            var name = choreById.TryGetValue(choreId, out var chore) ? chore.Name : string.Empty;
            firstEvers.Add(new FirstEver(name, LocalIsoDate(first)));
        }
        // Deterministic order — newest first-ever first (a fresh "first time!" leads the list).
        firstEvers = firstEvers
            .OrderByDescending(f => f.LocalDate)
            .ThenBy(f => f.ChoreName, StringComparer.Ordinal)
            .ToList();

        // Season totals = totals over the tiled window (sum of the week buckets), so they can never disagree
        // with the per-week aggregates (D10).
        var seasonCompletions = weeks.Sum(w => w.Completions);
        var seasonPoints = weeks.Sum(w => w.Points);

        return new HistoryMilestones(bestWeek, longestStreak, firstEvers, seasonCompletions, seasonPoints);
    }

    // ── Kept moments — completions carrying a note or photo (newest-first, capped) ───────────────────

    private IReadOnlyList<HistoryKeptMoment> BuildKeptMoments(
        IReadOnlyList<ChoreCompletion> completions,
        IReadOnlyDictionary<int, (string Name, int? RoomId)> choreById)
    {
        return completions
            .Where(c => !string.IsNullOrWhiteSpace(c.Note) || !string.IsNullOrWhiteSpace(c.PhotoPath))
            .OrderByDescending(c => c.CompletedAt)
            .Take(KeptMomentsCap)
            .Select(c => new HistoryKeptMoment(
                LocalDate: LocalIsoDate(c.CompletedAt),
                ChoreName: choreById.TryGetValue(c.ChoreId, out var chore) ? chore.Name : string.Empty,
                Note: string.IsNullOrWhiteSpace(c.Note) ? null : c.Note,
                HasPhoto: !string.IsNullOrWhiteSpace(c.PhotoPath)))
            .ToList();
    }

    // ── What got tended — per-room completion tally (roomless → "General") ───────────────────────────

    private static IReadOnlyList<HistoryRoomTally> BuildWhatGotTended(
        IReadOnlyList<ChoreCompletion> completions,
        IReadOnlyDictionary<int, (string Name, int? RoomId)> choreById,
        IReadOnlyDictionary<int, string> roomNameById)
    {
        var tally = new Dictionary<string, int>();
        foreach (var c in completions)
        {
            var roomName = ChoreRollup.GeneralGroupName;
            if (choreById.TryGetValue(c.ChoreId, out var chore)
                && chore.RoomId is { } roomId
                && roomNameById.TryGetValue(roomId, out var name))
            {
                roomName = name;
            }
            tally.TryGetValue(roomName, out var count);
            tally[roomName] = count + 1;
        }

        // Most-tended first; tie-break by name for determinism (fixture-stable).
        return tally
            .OrderByDescending(kvp => kvp.Value)
            .ThenBy(kvp => kvp.Key, StringComparer.Ordinal)
            .Select(kvp => new HistoryRoomTally(kvp.Key, kvp.Value))
            .ToList();
    }

    // ── Date helper (MN9 — server-stamped local ISO date) ────────────────────────────────────────────

    /// <summary>Format a UTC instant as a household-local ISO date (<c>yyyy-MM-dd</c>) — server-side so the
    /// island never builds a Date from a date-only string (MN9). Mirrors <c>ChoreRecapService.LocalIsoDate</c>.</summary>
    private string LocalIsoDate(DateTime utc)
    {
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        return local.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }
}
