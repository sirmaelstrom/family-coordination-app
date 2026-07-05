using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

/// <summary>
/// Which dueness state a chore is in, computed on read (WP-02). Distinct from the stored
/// <see cref="ChoreStatus"/> lifecycle enum (council M15).
/// </summary>
public enum DueState
{
    /// <summary>Recurs, but the next due point is in the future (not yet due).</summary>
    NotDue,

    /// <summary>The next due point falls on today's local date.</summary>
    DueToday,

    /// <summary>The due point is in the past (local date strictly before today).</summary>
    Overdue,

    /// <summary>Has a future schedule but no decay/dueness pressure yet (e.g. a fixed chore not near its slot).</summary>
    Scheduled
}

/// <summary>
/// Freshness color tier for the board (Fresh → Mid → Due → Overdue). Driven by the flexible-recurrence
/// decay band (P4) for flexible chores, and by relative dueness for fixed/one-off chores.
/// </summary>
public enum ColorTier
{
    Fresh,
    Mid,
    Due,
    Overdue
}

/// <summary>
/// The computed dueness/decay result for a chore (council M15 — named to avoid colliding with the
/// stored <see cref="ChoreStatus"/> enum). The island renders <see cref="ColorTier"/> / <see cref="DueState"/>
/// directly (M5/M6) — it does no date math of its own.
/// </summary>
public record ChoreDuenessResult(DueState DueState, ColorTier ColorTier, DateTime? NextDueAt)
{
    /// <summary>
    /// True iff a snooze floor is active (set AND today's local date is strictly before it). The single
    /// computed source of truth all attention surfaces (board/home/equity/digest) read (WP-04); copied onto
    /// the board DTO (WP-05). Init-only (not positional) so existing construction/deconstruction sites are
    /// untouched.
    /// </summary>
    public bool IsSnoozed { get; init; }
}

/// <summary>
/// Pure, dependency-free input snapshot of a chore's recurrence fields (WP-02). Decouples the
/// calculator from the EF <see cref="Chore"/> instance so it stays unit-pure and reusable by
/// WP-04 / WP-05.
/// </summary>
public readonly record struct ChoreRecurrenceSnapshot(
    RecurrenceMode RecurrenceMode,
    int? IntervalDays,
    DateOnly? AnchorDate,
    ChoreDaysOfWeek? DaysOfWeek,
    int? DayOfMonth,
    DateTime? LastCompletedAt,
    // Local-date snooze floor (null = no floor). Trailing + defaulted so existing 6-arg positional
    // construction sites keep compiling. See <see cref="Chore.SnoozedUntil"/>.
    DateOnly? SnoozedUntil = null)
{
    /// <summary>Project a <see cref="Chore"/> entity into a recurrence snapshot.</summary>
    public static ChoreRecurrenceSnapshot FromChore(Chore chore) => new(
        chore.RecurrenceMode,
        chore.IntervalDays,
        chore.AnchorDate,
        chore.DaysOfWeek,
        chore.DayOfMonth,
        chore.LastCompletedAt,
        chore.SnoozedUntil);
}

/// <summary>
/// Pure, timezone-aware calculator for chore dueness, freshness decay, and claim staleness
/// (D3/D4/D5/D7/D14). Takes UTC <see cref="DateTime"/> inputs (Kind=Utc — council M14), an injected
/// <c>now</c>, and an injected <see cref="TimeZoneInfo"/>. ALL day-boundary math is done in the
/// injected timezone's local day — there is no <see cref="DateTime.UtcNow"/> / <see cref="DateTime.Now"/>
/// reached for inside, and no <c>new Date('YYYY-MM-DD')</c>-style string parse (CORRECTIONS date footgun,
/// M5). No DB, no DI, no I/O.
/// </summary>
public class ChoreStatusCalculator
{
    /// <summary>How long a claim may sit before it is considered stale and eligible for auto-release (D7).</summary>
    public static readonly TimeSpan StalenessThreshold = TimeSpan.FromHours(48);

    // Flexible decay band (P4), expressed as fractions of the IntervalDays since last completion:
    //   [0, FreshUpperFraction)         -> Fresh
    //   [FreshUpperFraction, 1.0)       -> Mid
    //   [1.0, 1.0 + OverdueGraceFraction] -> Due  (the "due today / just lapsed" tolerance band)
    //   (> 1.0 + OverdueGraceFraction)  -> Overdue
    private const double FreshUpperFraction = 0.5;
    private const double OverdueGraceFraction = 0.0;

    /// <summary>
    /// Compute the dueness/decay state for a chore as of <paramref name="now"/> (UTC) in the given timezone.
    /// </summary>
    public ChoreDuenessResult Compute(ChoreRecurrenceSnapshot chore, DateTime now, TimeZoneInfo tz)
    {
        var today = LocalDate(now, tz);

        var natural = chore.RecurrenceMode switch
        {
            // Flexible/OneOff derive their next-due independently of the dueness branch; Fixed computes its
            // state and next-due together (completion-aware slot logic), so it is fully self-contained.
            // Fixed's covered-test and OneOff's effective-due already fold in SnoozedUntil (the skip rule /
            // reschedule); the gate below adds the universal suppression on top.
            RecurrenceMode.Flexible => ComputeFlexible(chore, now, tz, NextDueForFlexible(chore, today, tz)),
            RecurrenceMode.Fixed => ComputeFixed(chore, now, tz),
            RecurrenceMode.OneOff => ComputeOneOff(chore, now, tz, NextDueForOneOff(chore, tz)),
            _ => new ChoreDuenessResult(DueState.NotDue, ColorTier.Fresh, NextDueAt: null)
        };

        // Universal suppression gate: while today's local date is before the snooze floor, the chore is held
        // in a pressure-free Scheduled/Fresh state with IsSnoozed=true, and its public next-due is floored at
        // the snooze date (never earlier than the natural skip-aware next-due — the later of the two). On/after
        // the floor the natural result passes through unchanged with IsSnoozed=false.
        if (chore.SnoozedUntil is { } s && today < s)
        {
            return natural with
            {
                DueState = DueState.Scheduled,
                ColorTier = ColorTier.Fresh,
                NextDueAt = FloorAtSnooze(natural.NextDueAt, s, tz),
                IsSnoozed = true
            };
        }

        return natural;
    }

    /// <summary>
    /// The next moment (UTC, anchored to local-midnight of the next due local date) the chore comes due.
    /// Shared with WP-04 so the cadence-advance logic lives in one place. Returns null when the chore has
    /// no future cadence point (e.g. a satisfied one-off, or an unsupported / under-specified recurrence).
    /// </summary>
    public DateTime? ComputeNextDueAt(ChoreRecurrenceSnapshot chore, DateTime now, TimeZoneInfo tz)
    {
        var today = LocalDate(now, tz);

        var natural = chore.RecurrenceMode switch
        {
            RecurrenceMode.Flexible => NextDueForFlexible(chore, today, tz),
            // Delegate to the unified Fixed logic so the public next-due never diverges from the dueness it
            // reports (e.g. a covered slot rolls forward; an overdue slot points at the missed date). Fixed's
            // skip-rule already folds SnoozedUntil into the slot; OneOff's effective-due likewise.
            RecurrenceMode.Fixed => ComputeFixed(chore, now, tz).NextDueAt,
            RecurrenceMode.OneOff => NextDueForOneOff(chore, tz),
            _ => null
        };

        // Mirror Compute's gate so the public next-due never diverges from the gated dueness.
        if (chore.SnoozedUntil is { } s && today < s)
        {
            return FloorAtSnooze(natural, s, tz);
        }

        return natural;
    }

    /// <summary>
    /// True only when a chore's claim has gone stale: it must be <see cref="AssignmentKind.Claimed"/> AND
    /// have a non-null <paramref name="claimedAt"/> AND have sat longer than <see cref="StalenessThreshold"/>.
    /// Never throws — a dropped chore can momentarily be <see cref="AssignmentKind.Claimed"/> with a null
    /// <paramref name="claimedAt"/>; that returns false (council M2). An assigned chore is never stale.
    /// </summary>
    public bool IsClaimStale(AssignmentKind kind, DateTime? claimedAt, DateTime now)
    {
        if (kind != AssignmentKind.Claimed || claimedAt is null)
        {
            return false;
        }

        return claimedAt.Value + StalenessThreshold < now;
    }

    // ---- Flexible: decay relative to last completion -------------------------------------------------

    private static ChoreDuenessResult ComputeFlexible(
        ChoreRecurrenceSnapshot chore, DateTime now, TimeZoneInfo tz, DateTime? nextDueAt)
    {
        // No interval (or non-positive) => no decay model; treat as fresh / not due.
        if (chore.IntervalDays is not { } interval || interval <= 0)
        {
            return new ChoreDuenessResult(DueState.NotDue, ColorTier.Fresh, nextDueAt);
        }

        // Never completed => the chore is due now (first occurrence pressure).
        if (chore.LastCompletedAt is not { } lastCompleted)
        {
            return new ChoreDuenessResult(DueState.DueToday, ColorTier.Due, nextDueAt);
        }

        var today = LocalDate(now, tz);
        var completedLocalDate = LocalDate(lastCompleted, tz);
        var daysSince = (today.DayNumber - completedLocalDate.DayNumber);

        // Whole-local-day age as a fraction of the interval (the decay band, P4).
        var fraction = (double)daysSince / interval;

        ColorTier tier;
        DueState state;
        if (fraction < FreshUpperFraction)
        {
            tier = ColorTier.Fresh;
            state = DueState.NotDue;
        }
        else if (fraction < 1.0)
        {
            tier = ColorTier.Mid;
            state = DueState.NotDue;
        }
        else if (fraction <= 1.0 + OverdueGraceFraction)
        {
            tier = ColorTier.Due;
            state = DueState.DueToday;
        }
        else
        {
            tier = ColorTier.Overdue;
            state = DueState.Overdue;
        }

        return new ChoreDuenessResult(state, tier, nextDueAt);
    }

    private static DateTime? NextDueForFlexible(ChoreRecurrenceSnapshot chore, DateOnly today, TimeZoneInfo tz)
    {
        if (chore.IntervalDays is not { } interval || interval <= 0)
        {
            return null;
        }

        // Flexible cadence is anchored to the last completion; with no completion the next due is today.
        var baseDate = chore.LastCompletedAt is { } last
            ? LocalDate(last, tz).AddDays(interval)
            : today;

        return LocalMidnightUtc(baseDate, tz);
    }

    // ---- Fixed: interval-from-anchor OR weekly-on-weekday (completion-aware) -------------------------
    //
    // A fixed chore is tied to concrete calendar slots (every-N days from an anchor, or specific weekdays).
    // Dueness is computed against the CURRENT slot — the most recent cadence date on or before today — and
    // whether a completion has satisfied it:
    //   • current slot already covered by a completion        -> Scheduled, rolled forward to the next slot
    //   • current slot is today and not yet done              -> DueToday
    //   • current slot is in the PAST, not done, and the chore
    //     has a prior-completion baseline                     -> Overdue (lands in "Falling behind")
    // A single completion on or after the current slot's date satisfies it and advances to the next future
    // occurrence — so catching up after several missed slots takes ONE "Done", not one per missed day
    // (feedback: forgetting the mail for 3 days should not require 3 completions). A never-completed fixed
    // chore is treated as "new" — never retroactively overdue for a slot that predates it — though it can
    // still read DueToday on a cadence day.

    private static ChoreDuenessResult ComputeFixed(ChoreRecurrenceSnapshot chore, DateTime now, TimeZoneInfo tz)
    {
        var today = LocalDate(now, tz);
        var (currentSlot, nextSlot) = FixedSlots(chore, today);

        DateTime? AsUtc(DateOnly? date) => date is { } d ? LocalMidnightUtc(d, tz) : null;

        // Under-specified fixed chore (no DaysOfWeek, no anchor+interval) => benign Scheduled, no due point.
        if (currentSlot is null && nextSlot is null)
        {
            return new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, NextDueAt: null);
        }

        // Schedule hasn't started yet (anchor in the future) => waiting on the first occurrence.
        if (currentSlot is not { } slot)
        {
            return new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, AsUtc(nextSlot));
        }

        // Has the current slot already been satisfied? A completion on or after the slot's LOCAL date covers
        // it — and because we only ever test the CURRENT slot, one completion clears however many slots have
        // elapsed since the last one (the catch-up property). A snooze floor STRICTLY past the slot also
        // covers it (the skip rule: "not this Monday") — keyed on s > slot so snoozing to the slot's own date
        // is a no-op. SnoozedUntil is never cleared here.
        var covered = (chore.LastCompletedAt is { } last && LocalDate(last, tz) >= slot)
            || (chore.SnoozedUntil is { } s && s > slot);
        if (covered)
        {
            return new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, AsUtc(nextSlot));
        }

        // Pending. Today's occurrence reads DueToday; a past, uncovered occurrence reads Overdue — but only
        // once the chore has a completion baseline (a brand-new chore is "new", not retroactively behind).
        if (slot == today)
        {
            return new ChoreDuenessResult(DueState.DueToday, ColorTier.Due, AsUtc(today));
        }

        if (chore.LastCompletedAt is not null)
        {
            return new ChoreDuenessResult(DueState.Overdue, ColorTier.Overdue, AsUtc(slot));
        }

        return new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, AsUtc(nextSlot));
    }

    /// <summary>
    /// The current and next fixed-cadence slots relative to <paramref name="today"/> (local dates).
    /// <c>Current</c> is the most recent cadence date on or before today (null when the schedule starts in the
    /// future); <c>Next</c> is the first cadence date strictly after today. Returns (null, null) for an
    /// under-specified fixed recurrence (e.g. monthly-on-day, which is unsupported here).
    /// </summary>
    private static (DateOnly? Current, DateOnly? Next) FixedSlots(ChoreRecurrenceSnapshot chore, DateOnly today)
    {
        // Weekly-on-weekday (D4-B): scan a 7-day window each way for a flagged weekday. A non-empty flag set
        // always matches within 7 days, so Current is never null for weekly.
        if (chore.DaysOfWeek is { } days && days != ChoreDaysOfWeek.None)
        {
            DateOnly? current = null;
            for (var back = 0; back < 7; back++)
            {
                var candidate = today.AddDays(-back);
                if (days.HasFlag(ToChoreFlag(candidate.DayOfWeek)))
                {
                    current = candidate;
                    break;
                }
            }

            DateOnly? next = null;
            for (var fwd = 1; fwd <= 7; fwd++)
            {
                var candidate = today.AddDays(fwd);
                if (days.HasFlag(ToChoreFlag(candidate.DayOfWeek)))
                {
                    next = candidate;
                    break;
                }
            }

            return (current, next);
        }

        // Interval-from-anchor (every-N days): the cadence is anchor + k·interval (k >= 0).
        if (chore.AnchorDate is { } anchor && chore.IntervalDays is { } interval && interval > 0)
        {
            if (today < anchor)
            {
                // Not started yet — the anchor itself is the first (next) slot, with no current slot.
                return (null, anchor);
            }

            var k = (today.DayNumber - anchor.DayNumber) / interval;   // floor => most recent slot <= today
            var current = anchor.AddDays(k * interval);
            var next = anchor.AddDays((k + 1) * interval);
            return (current, next);
        }

        // Under-specified (e.g. DayOfMonth-only) — unsupported here; no slots.
        return (null, null);
    }

    // ---- OneOff: due against AnchorDate, never recurs ------------------------------------------------

    private static ChoreDuenessResult ComputeOneOff(
        ChoreRecurrenceSnapshot chore, DateTime now, TimeZoneInfo tz, DateTime? nextDueAt)
    {
        // Already completed => not due, no advance.
        if (chore.LastCompletedAt is not null)
        {
            return new ChoreDuenessResult(DueState.NotDue, ColorTier.Fresh, NextDueAt: null);
        }

        // Snooze floor REPLACES the anchor for OneOff dueness when set (one effective-due source of truth —
        // the universal gate handles only the today < s suppression; == s ⇒ DueToday, > s ⇒ Overdue fall out
        // of comparing today to effDue). Preserves the null-anchor early return when both are null.
        var effDue = chore.SnoozedUntil ?? chore.AnchorDate;

        // No due date => never overdue, no NextDueAt advance.
        if (effDue is not { } due)
        {
            return new ChoreDuenessResult(DueState.NotDue, ColorTier.Fresh, NextDueAt: null);
        }

        var today = LocalDate(now, tz);
        if (due == today)
        {
            return new ChoreDuenessResult(DueState.DueToday, ColorTier.Due, nextDueAt);
        }

        if (due < today)
        {
            return new ChoreDuenessResult(DueState.Overdue, ColorTier.Overdue, nextDueAt);
        }

        // Future due date => Scheduled (NOT NotDue), matching the Fixed cadence paths above. NotDue means
        // "recurs, no pending point"; a one-off WITH a future due date has a concrete pending slot, so it
        // reads as "Scheduled" on the card (renders "Scheduled" instead of the bare "On track"). This keeps
        // future-dated one-offs (feedback #5) consistent with future-dated fixed chores.
        return new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, nextDueAt);
    }

    private static DateTime? NextDueForOneOff(ChoreRecurrenceSnapshot chore, TimeZoneInfo tz)
    {
        // A satisfied one-off has no future due point.
        if (chore.LastCompletedAt is not null)
        {
            return null;
        }

        // Mirror ComputeOneOff: the snooze floor replaces the anchor as the effective due date.
        var effDue = chore.SnoozedUntil ?? chore.AnchorDate;
        return effDue is { } due ? LocalMidnightUtc(due, tz) : null;
    }

    // ---- Date helpers (all day-boundary math is timezone-aware) --------------------------------------

    /// <summary>The local calendar date of a UTC instant in the injected timezone.
    /// <para><c>internal</c> (Phase 15 WP-01) so <c>ChoreHistoryService</c> reuses the single DST-correct
    /// source for its projection math (M4) instead of re-deriving it. Visibility-only — no behavior change.</para></summary>
    internal static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz)
    {
        var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);
        return DateOnly.FromDateTime(local);
    }

    /// <summary>The UTC instant corresponding to local-midnight (00:00) of the given local date.
    /// <para><c>internal</c> (Phase 15 WP-01) — the projection converts each generated <c>DateOnly</c> beat to
    /// UTC solely through this helper (M4). Visibility-only — no behavior change.</para></summary>
    internal static DateTime LocalMidnightUtc(DateOnly localDate, TimeZoneInfo tz)
    {
        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
    }

    /// <summary>
    /// The snooze-aware next-due: the later of the natural (skip-aware) next-due and local-midnight of the
    /// snooze floor. Shared by <see cref="Compute"/> and <see cref="ComputeNextDueAt"/> so the two never
    /// diverge. When the natural next-due is null (e.g. an under-specified recurrence), the floor is used.
    /// </summary>
    private static DateTime FloorAtSnooze(DateTime? naturalNextDue, DateOnly snoozedUntil, TimeZoneInfo tz)
    {
        var resumeFloor = LocalMidnightUtc(snoozedUntil, tz);
        return naturalNextDue is { } nd && nd > resumeFloor ? nd : resumeFloor;
    }

    /// <summary>Map a <see cref="System.DayOfWeek"/> to the project's custom <see cref="ChoreDaysOfWeek"/> flag.
    /// <para><c>internal</c> (Phase 15 WP-01) so the weekly-cadence beat generator flag-matches local dates via
    /// the same map. Visibility-only — no behavior change.</para></summary>
    internal static ChoreDaysOfWeek ToChoreFlag(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => ChoreDaysOfWeek.Sunday,
        DayOfWeek.Monday => ChoreDaysOfWeek.Monday,
        DayOfWeek.Tuesday => ChoreDaysOfWeek.Tuesday,
        DayOfWeek.Wednesday => ChoreDaysOfWeek.Wednesday,
        DayOfWeek.Thursday => ChoreDaysOfWeek.Thursday,
        DayOfWeek.Friday => ChoreDaysOfWeek.Friday,
        DayOfWeek.Saturday => ChoreDaysOfWeek.Saturday,
        _ => ChoreDaysOfWeek.None
    };
}
