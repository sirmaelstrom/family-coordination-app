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
    TimeZoneInfo tz,
    TimeProvider timeProvider) : IChoreHistoryService
{
    private const int MinWeeks = 1;
    private const int MaxWeeks = 26;

    /// <summary>Fallback display name for a completion whose completer is no longer a household member.</summary>
    private const string UnknownDoerName = "A household member";

    /// <summary>The "kept moments" highlight reel is capped so the payload stays small (newest-first).</summary>
    private const int KeptMomentsCap = 12;

    /// <summary>
    /// A chore enters the gone-quiet band at ≥2 consecutive TRAILING missed beats (D10). One miss is a blip;
    /// two-plus trailing is a pattern of silence. First-pass value, fixture-locked — tuning is cheap because
    /// beat generation is separate from gone-quiet detection.
    /// </summary>
    private const int GoneQuietThreshold = 2;

    /// <summary>
    /// The projection read reaches this many days before the aggregation window so a beat at the window's
    /// leading edge can still be "kept" by a completion that landed just before the window (the widest per-mode
    /// back tolerance is 7 — <see cref="IntervalBackDays"/>'s clamp ceiling). Bounded (M2) — a small constant
    /// margin, not O(all history).
    /// </summary>
    private const int MaxBackToleranceDays = 7;

    // ── Per-mode match tolerances (D8) — named, fixture-locked. How many days a completion may land from a
    //    beat and still "keep" it. Tuning is cheap because generation (ProjectBeats) is separate from matching.
    private const int WeeklyBackDays = 1;
    private const int WeeklyForwardDays = 3;
    private const int OneOffBackDays = 1;
    private const int OneOffForwardDays = 14;
    private static int IntervalBackDays(int intervalDays) => Math.Clamp(intervalDays / 4, 1, 7);
    private static int IntervalForwardDays(int intervalDays) => Math.Clamp(intervalDays / 2, 1, 14);
    private const int FlexibleBackDays = 1;
    private static int FlexibleForwardDays(int intervalDays) => Math.Clamp(intervalDays / 2, 1, 14);

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

        // Active chores drive the gap projection (WP-02). Read the full entities (recurrence fields +
        // SnoozedUntil + CreatedAt + LastCompletedAt) — O(active chores), scoped to the household (M1).
        var activeChores = await context.Chores
            .AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.Status == ChoreStatus.Active)
            .ToListAsync(ct);

        // Completion dates for matching. Read back a small margin before the window (MaxBackToleranceDays) so a
        // beat at the window's leading edge can be kept by a completion just before it (no false leading-edge
        // ghost). Bounded (M2). Lightweight projection ({ChoreId, CompletedAt}) — grouped into per-chore local
        // dates for the greedy matcher.
        var projectionReadStartUtc = oldestWeekStartUtc.AddDays(-MaxBackToleranceDays);
        var projectionCompletionRows = await context.ChoreCompletions
            .AsNoTracking()
            .Where(c => c.HouseholdId == householdId && c.CompletedAt >= projectionReadStartUtc)
            .Select(c => new { c.ChoreId, c.CompletedAt })
            .ToListAsync(ct);
        var doneDatesByChore = projectionCompletionRows
            .GroupBy(r => r.ChoreId)
            .ToDictionary(
                g => g.Key,
                g => g.Select(r => ChoreStatusCalculator.LocalDate(r.CompletedAt, tz)).OrderBy(d => d).ToList());

        // Snooze log (WP-03): the accurate snoozed-vs-slipped reason for each ghost/gone-quiet chore comes from
        // the ChoreSnoozeEvent stream, not current state. Two bounded reads (M2 exception b):
        //   (1) in-window events (SetAt >= windowStart);
        //   (2) the SINGLE latest event before the window per chore (MAX(SetAt) GROUP BY ChoreId, re-fetched),
        //       seeding the snooze state active AT windowStart so an early-window ghost inside a pre-window
        //       snooze isn't mislabeled.
        var snoozeEventRows = await context.ChoreSnoozeEvents
            .AsNoTracking()
            .Where(e => e.HouseholdId == householdId && e.SetAt >= oldestWeekStartUtc)
            .Select(e => new SnoozeRow(e.ChoreId, e.SetAt, e.SnoozedUntil))
            .ToListAsync(ct);

        var seedMaxes = await context.ChoreSnoozeEvents
            .AsNoTracking()
            .Where(e => e.HouseholdId == householdId && e.SetAt < oldestWeekStartUtc)
            .GroupBy(e => e.ChoreId)
            .Select(g => new { ChoreId = g.Key, MaxSetAt = g.Max(e => e.SetAt) })
            .ToListAsync(ct);

        var seedRows = new List<SnoozeRow>();
        if (seedMaxes.Count > 0)
        {
            var maxSetAts = seedMaxes.Select(s => s.MaxSetAt).Distinct().ToList();
            var candidates = await context.ChoreSnoozeEvents
                .AsNoTracking()
                .Where(e => e.HouseholdId == householdId && e.SetAt < oldestWeekStartUtc && maxSetAts.Contains(e.SetAt))
                .Select(e => new SnoozeRow(e.ChoreId, e.SetAt, e.SnoozedUntil))
                .ToListAsync(ct);
            // Keep exactly the (ChoreId, MaxSetAt) rows — guards against a rare cross-chore SetAt collision.
            var seedKeys = seedMaxes.Select(s => (s.ChoreId, s.MaxSetAt)).ToHashSet();
            seedRows = candidates.Where(c => seedKeys.Contains((c.ChoreId, c.SetAt))).ToList();
        }

        var snoozeByChore = BuildSnoozeStates(snoozeEventRows, seedRows);

        var memberNameById = members.ToDictionary(m => m.UserId, m => m.DisplayName);

        var events = BuildEvents(completions, choreById, memberNameById);
        var weekBuckets = BucketByWeek(completions, asOf, weeks);
        var weeksResult = BuildWeeks(weekBuckets, members, asOf);
        var milestones = BuildMilestones(weeksResult, firstEverByChore, choreById, oldestWeekStartUtc, asOf);
        var keptMoments = BuildKeptMoments(completions, choreById);
        var whatGotTended = BuildWhatGotTended(completions, choreById, roomNameById);
        var (ghosts, goneQuiet) = BuildProjection(activeChores, doneDatesByChore, snoozeByChore, oldestWeekStartUtc, asOf);

        return new ChoreHistoryResult(
            WindowStartLocal: LocalIsoDate(oldestWeekStartUtc),
            WindowEndLocal: LocalIsoDate(asOf),
            Events: events,
            Weeks: weeksResult,
            Ghosts: ghosts,
            GoneQuiet: goneQuiet,
            Milestones: milestones,
            KeptMoments: keptMoments,
            WhatGotTended: whatGotTended);
    }

    // ── Historical gap/ghost projection (WP-02) ──────────────────────────────────────────────────────
    //
    // For each active recurring chore, project the NOMINAL expected beats across the window in DateOnly space
    // (M4/MN4 — never UTC-tick arithmetic), greedily match them to completions within a per-mode tolerance, and
    // emit each unmatched beat as a ghost. A chore with ≥2 consecutive trailing misses becomes gone-quiet.
    // Snooze does NOT suppress generation (MN7) — it only labels the reason (a current-state placeholder here;
    // WP-03 overwrites it from the ChoreSnoozeEvent log). Matching does NOT collapse the way the live board does
    // (MN6) — every missed beat is surfaced.

    private (IReadOnlyList<HistoryGhost> Ghosts, IReadOnlyList<HistoryGoneQuiet> GoneQuiet) BuildProjection(
        IReadOnlyList<Chore> activeChores,
        IReadOnlyDictionary<int, List<DateOnly>> doneDatesByChore,
        IReadOnlyDictionary<int, ChoreSnoozeState> snoozeByChore,
        DateTime oldestWeekStartUtc,
        DateTime asOf)
    {
        var aggWindowStart = ChoreStatusCalculator.LocalDate(oldestWeekStartUtc, tz);
        var today = ChoreStatusCalculator.LocalDate(asOf, tz);

        var ghosts = new List<HistoryGhost>();
        var goneQuiet = new List<HistoryGoneQuiet>();

        foreach (var chore in activeChores)
        {
            var snap = ChoreRecurrenceSnapshot.FromChore(chore);

            // Monthly (DayOfMonth-only) is rejected in v1 (MN5) — never project it. ProjectBeats also returns
            // empty for it, but skip explicitly so intent is legible.
            if (IsMonthlyOnly(snap))
            {
                continue;
            }

            // Never invent a beat before the chore existed: floor the window at the chore's creation date.
            var choreCreated = ChoreStatusCalculator.LocalDate(chore.CreatedAt, tz);
            var windowStart = choreCreated > aggWindowStart ? choreCreated : aggWindowStart;

            var beats = ProjectBeats(snap, windowStart, today, tz);
            if (beats.Count == 0)
            {
                continue;
            }

            var doneDates = doneDatesByChore.TryGetValue(chore.ChoreId, out var d) ? d : new List<DateOnly>();
            var (back, fwd) = ToleranceFor(snap);
            var (_, missed) = Match(beats, doneDates, back, fwd);

            snoozeByChore.TryGetValue(chore.ChoreId, out var snoozeState);

            var missedSet = missed.ToHashSet();
            foreach (var beat in missed)
            {
                // WP-03: the accurate reason comes from the ChoreSnoozeEvent log (ReasonFromLog=true); beats
                // before the chore's earliest logged event fall back to current state (ReasonFromLog=false).
                var (reason, fromLog) = ResolveReason(snap, beat, snoozeState);
                ghosts.Add(new HistoryGhost(
                    ChoreName: chore.Name,
                    ExpectedLocalDate: Iso(beat),
                    Reason: reason,
                    ReasonFromLog: fromLog));
            }

            // Gone-quiet: the trailing run of consecutive misses at the newest end of the beat list.
            var trailing = TrailingMissCount(beats, missedSet);
            if (trailing >= GoneQuietThreshold)
            {
                var trailingBeats = beats.GetRange(beats.Count - trailing, trailing);
                // Snoozed iff EVERY trailing missed beat was snoozed (log-derived); else slipped (D5).
                var allSnoozed = trailingBeats.All(b => ResolveReason(snap, b, snoozeState).Reason == "snoozed");
                goneQuiet.Add(new HistoryGoneQuiet(
                    ChoreName: chore.Name,
                    CadenceLabel: CadenceLabel(snap),
                    LastCompletedLocalDate: chore.LastCompletedAt is { } lc
                        ? Iso(ChoreStatusCalculator.LocalDate(lc, tz))
                        : null,
                    Reason: allSnoozed ? "snoozed" : "slipped"));
            }
        }

        // Deterministic order (fixture-stable): ghosts by date then chore; gone-quiet by chore.
        ghosts = ghosts
            .OrderBy(g => g.ExpectedLocalDate, StringComparer.Ordinal)
            .ThenBy(g => g.ChoreName, StringComparer.Ordinal)
            .ToList();
        goneQuiet = goneQuiet
            .OrderBy(g => g.ChoreName, StringComparer.Ordinal)
            .ToList();

        return (ghosts, goneQuiet);
    }

    /// <summary>
    /// Project the NOMINAL expected beats for a chore across <c>[windowStart, today]</c>, generated entirely in
    /// <see cref="DateOnly"/> (local-calendar) space so DST can never shift a beat off its weekday (M4 — the
    /// load-bearing rule; UTC conversion happens only at the render edge). Mirrors the validated projection
    /// spike verbatim. Snooze is ignored for GENERATION (MN7). <c>internal</c> for direct unit testing.
    /// </summary>
    internal static List<DateOnly> ProjectBeats(
        ChoreRecurrenceSnapshot chore, DateOnly windowStart, DateOnly today, TimeZoneInfo tz)
    {
        var beats = new List<DateOnly>();
        switch (chore.RecurrenceMode)
        {
            // Fixed weekly (DaysOfWeek): every flagged local weekday in the window is a calendar slot. Interior
            // misses accumulate (unlike Flexible). Respect AnchorDate as a lower bound when set.
            case RecurrenceMode.Fixed when chore.DaysOfWeek is { } days && days != ChoreDaysOfWeek.None:
                for (var d = windowStart; d <= today; d = d.AddDays(1))
                {
                    if (days.HasFlag(ChoreStatusCalculator.ToChoreFlag(d.DayOfWeek))
                        && (chore.AnchorDate is not { } a || d >= a))
                    {
                        beats.Add(d);
                    }
                }
                break;

            // Fixed interval-from-anchor: anchor + k·interval, stepped in DateOnly space (DST-immune).
            case RecurrenceMode.Fixed when chore.AnchorDate is { } anchor && chore.IntervalDays is { } iv && iv > 0:
                for (var k = 0; ; k++)
                {
                    var d = anchor.AddDays(k * iv);
                    if (d > today)
                    {
                        break;
                    }
                    if (d >= windowStart)
                    {
                        beats.Add(d);
                    }
                }
                break;

            // Flexible is COMPLETION-anchored: the clock resets on every completion, so only the TRAILING run
            // after the last completion accumulates missed beats. A late-but-done interior gap is a late
            // completion, NOT a missed beat (fundamentally different from Fixed's calendar slots).
            case RecurrenceMode.Flexible when chore.IntervalDays is { } iv && iv > 0:
                var lastDone = chore.LastCompletedAt is { } last
                    ? ChoreStatusCalculator.LocalDate(last, tz)
                    : chore.AnchorDate; // never completed: pressure starts at the anchor
                if (lastDone is { } baseDate)
                {
                    for (var d = baseDate.AddDays(iv); d <= today; d = d.AddDays(iv))
                    {
                        if (d >= windowStart)
                        {
                            beats.Add(d);
                        }
                    }
                }
                break;

            // OneOff: HISTORY projects the ORIGINAL anchor, NOT the snooze-replaced due date — surfacing what
            // snooze hid is the whole point (diverges from production OneOff dueness which reschedules).
            case RecurrenceMode.OneOff:
                if (chore.AnchorDate is { } due && due >= windowStart && due <= today && chore.LastCompletedAt is null)
                {
                    beats.Add(due);
                }
                break;
        }
        return beats;
    }

    /// <summary>
    /// Greedily match beats to completions: each beat claims the NEAREST not-yet-consumed completion whose local
    /// date is within <c>[beat − backTol, beat + fwdTol]</c> (ties → the earlier completion); a completion is
    /// consumed by at most one beat. Unmatched beats are missed. Deliberately does NOT collapse the way the live
    /// board does (MN6) — the history surface shows every missed beat. <c>internal</c> for direct unit testing.
    /// </summary>
    internal static (List<DateOnly> Kept, List<DateOnly> Missed) Match(
        IReadOnlyList<DateOnly> beats, IReadOnlyList<DateOnly> doneDates, int backTol, int fwdTol)
    {
        var kept = new List<DateOnly>();
        var missed = new List<DateOnly>();
        var used = new bool[doneDates.Count];
        foreach (var beat in beats)
        {
            var bestIdx = -1;
            var bestDist = int.MaxValue;
            for (var i = 0; i < doneDates.Count; i++)
            {
                if (used[i])
                {
                    continue;
                }
                var delta = doneDates[i].DayNumber - beat.DayNumber; // + = completed after the beat
                if (delta < -backTol || delta > fwdTol)
                {
                    continue;
                }
                var dist = Math.Abs(delta);
                // doneDates is ascending, so the FIRST minimal-distance hit is the earliest (tie → earlier).
                if (dist < bestDist)
                {
                    bestDist = dist;
                    bestIdx = i;
                }
            }
            if (bestIdx >= 0)
            {
                used[bestIdx] = true;
                kept.Add(beat);
            }
            else
            {
                missed.Add(beat);
            }
        }
        return (kept, missed);
    }

    /// <summary>The count of consecutive missed beats at the NEWEST (trailing) end of the ordered beat list.</summary>
    internal static int TrailingMissCount(IReadOnlyList<DateOnly> beats, ISet<DateOnly> missedSet)
    {
        var trailing = 0;
        for (var i = beats.Count - 1; i >= 0; i--)
        {
            if (!missedSet.Contains(beats[i]))
            {
                break;
            }
            trailing++;
        }
        return trailing;
    }

    /// <summary>The per-mode match tolerance (D8), keyed off the recurrence shape.</summary>
    private static (int Back, int Fwd) ToleranceFor(ChoreRecurrenceSnapshot chore)
    {
        var iv = chore.IntervalDays ?? 0;
        return chore.RecurrenceMode switch
        {
            RecurrenceMode.Fixed when chore.DaysOfWeek is { } days && days != ChoreDaysOfWeek.None
                => (WeeklyBackDays, WeeklyForwardDays),
            RecurrenceMode.Fixed => (IntervalBackDays(iv), IntervalForwardDays(iv)),
            RecurrenceMode.Flexible => (FlexibleBackDays, FlexibleForwardDays(iv)),
            RecurrenceMode.OneOff => (OneOffBackDays, OneOffForwardDays),
            _ => (WeeklyBackDays, WeeklyForwardDays),
        };
    }

    /// <summary>A human cadence label for the gone-quiet card ("Mon/Thu" | "every N days" | "one-off").</summary>
    private static string CadenceLabel(ChoreRecurrenceSnapshot chore)
    {
        if (chore.RecurrenceMode == RecurrenceMode.OneOff)
        {
            return "one-off";
        }
        if (chore.RecurrenceMode == RecurrenceMode.Fixed
            && chore.DaysOfWeek is { } days && days != ChoreDaysOfWeek.None)
        {
            var abbr = new[]
            {
                (ChoreDaysOfWeek.Sunday, "Sun"), (ChoreDaysOfWeek.Monday, "Mon"), (ChoreDaysOfWeek.Tuesday, "Tue"),
                (ChoreDaysOfWeek.Wednesday, "Wed"), (ChoreDaysOfWeek.Thursday, "Thu"), (ChoreDaysOfWeek.Friday, "Fri"),
                (ChoreDaysOfWeek.Saturday, "Sat"),
            };
            return string.Join("/", abbr.Where(x => days.HasFlag(x.Item1)).Select(x => x.Item2));
        }
        return chore.IntervalDays is { } iv && iv > 0 ? $"every {iv} days" : "recurring";
    }

    /// <summary>Whether a chore is a monthly (<c>DayOfMonth</c>-only) recurrence — OUT OF SCOPE in v1 (MN5).</summary>
    private static bool IsMonthlyOnly(ChoreRecurrenceSnapshot chore) =>
        chore.DayOfMonth is not null
        && (chore.DaysOfWeek is null || chore.DaysOfWeek == ChoreDaysOfWeek.None)
        && !(chore.AnchorDate is not null && chore.IntervalDays is > 0);

    /// <summary>
    /// The CURRENT-STATE placeholder reason (WP-02): a beat is <c>snoozed</c> iff a snooze floor is set now and
    /// the beat predates it, else <c>slipped</c>. Coarse — a slip-then-snooze paints the whole gap "snoozed"
    /// (spike S8); WP-03 replaces this with the accurate log-derived reason.
    /// </summary>
    private static string CurrentStateReason(ChoreRecurrenceSnapshot chore, DateOnly beat) =>
        chore.SnoozedUntil is { } s && beat < s ? "snoozed" : "slipped";

    /// <summary>Format a <see cref="DateOnly"/> beat as an ISO date string (already a local date — no tz conversion).</summary>
    private static string Iso(DateOnly d) => d.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    // ── Snooze log fold (WP-03) ──────────────────────────────────────────────────────────────────────

    /// <summary>A single snooze-log row (a SET when <c>SnoozedUntil</c> is non-null, a CLEAR when null).</summary>
    private sealed record SnoozeRow(int ChoreId, DateTime SetAt, DateOnly? SnoozedUntil);

    /// <summary>
    /// A chore's folded snooze state: the active snooze intervals (UTC half-open <c>[start, end)</c>) plus the
    /// local date of the earliest loaded event — the coverage boundary. A ghost on/after
    /// <see cref="EarliestEventLocalDate"/> is labeled from the log; one before it falls back to current state.
    /// </summary>
    private sealed record ChoreSnoozeState(
        IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> Intervals,
        DateOnly? EarliestEventLocalDate);

    /// <summary>Group the (seed + in-window) snooze rows per chore and fold each into snooze intervals.</summary>
    private Dictionary<int, ChoreSnoozeState> BuildSnoozeStates(
        IReadOnlyList<SnoozeRow> inWindow, IReadOnlyList<SnoozeRow> seeds)
    {
        var byChore = new Dictionary<int, List<SnoozeRow>>();
        foreach (var r in seeds.Concat(inWindow))
        {
            if (!byChore.TryGetValue(r.ChoreId, out var list))
            {
                list = new List<SnoozeRow>();
                byChore[r.ChoreId] = list;
            }
            list.Add(r);
        }

        var result = new Dictionary<int, ChoreSnoozeState>(byChore.Count);
        foreach (var (choreId, rows) in byChore)
        {
            var ordered = rows.OrderBy(r => r.SetAt).ToList();
            var intervals = FoldSnoozeIntervals(ordered);
            var earliest = ChoreStatusCalculator.LocalDate(ordered[0].SetAt, tz);
            result[choreId] = new ChoreSnoozeState(intervals, earliest);
        }
        return result;
    }

    /// <summary>
    /// Fold an ordered event stream into active snooze intervals (D5). A SET at <c>t0</c> with floor <c>X</c>
    /// opens <c>[t0, min(LocalMidnightUtc(X), nextEventSetAt))</c> — the snooze expires at its own floor OR is
    /// superseded by the next event (a CLEAR or a re-snooze), whichever is earlier. A CLEAR opens no interval;
    /// it only bounds the preceding SET via <c>nextEventSetAt</c>. Using the next EVENT (not just the next
    /// CLEAR) also cleanly handles a re-snooze that shortens the prior floor.
    /// </summary>
    private List<(DateTime StartUtc, DateTime EndUtc)> FoldSnoozeIntervals(IReadOnlyList<SnoozeRow> ordered)
    {
        var intervals = new List<(DateTime, DateTime)>();
        for (var i = 0; i < ordered.Count; i++)
        {
            if (ordered[i].SnoozedUntil is not { } floor)
            {
                continue; // CLEAR — opens nothing.
            }
            var startUtc = DateTime.SpecifyKind(ordered[i].SetAt, DateTimeKind.Utc);
            var floorUtc = ChoreStatusCalculator.LocalMidnightUtc(floor, tz);
            var nextEventUtc = i + 1 < ordered.Count
                ? DateTime.SpecifyKind(ordered[i + 1].SetAt, DateTimeKind.Utc)
                : DateTime.MaxValue;
            var endUtc = floorUtc < nextEventUtc ? floorUtc : nextEventUtc;
            if (endUtc > startUtc)
            {
                intervals.Add((startUtc, endUtc));
            }
        }
        return intervals;
    }

    /// <summary>Whether a beat's local day <c>[midnight(D), midnight(D+1))</c> intersects any snooze interval.</summary>
    private bool WasSnoozedAt(DateOnly beat, IReadOnlyList<(DateTime StartUtc, DateTime EndUtc)> intervals)
    {
        var dayStart = ChoreStatusCalculator.LocalMidnightUtc(beat, tz);
        var dayEnd = ChoreStatusCalculator.LocalMidnightUtc(beat.AddDays(1), tz);
        foreach (var (startUtc, endUtc) in intervals)
        {
            if (startUtc < dayEnd && endUtc > dayStart)
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// Resolve a beat's reason. If the snooze log covers the beat (the chore has events AND the beat is on/after
    /// the earliest loaded event), the answer is log-derived (<c>ReasonFromLog=true</c>). Otherwise it falls
    /// back to the current-state placeholder (<c>ReasonFromLog=false</c>) — best-effort for pre-log beats (D5).
    /// </summary>
    private (string Reason, bool FromLog) ResolveReason(
        ChoreRecurrenceSnapshot chore, DateOnly beat, ChoreSnoozeState? state)
    {
        if (state is { EarliestEventLocalDate: { } earliest } && beat >= earliest)
        {
            return (WasSnoozedAt(beat, state.Intervals) ? "snoozed" : "slipped", true);
        }
        return (CurrentStateReason(chore, beat), false);
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
