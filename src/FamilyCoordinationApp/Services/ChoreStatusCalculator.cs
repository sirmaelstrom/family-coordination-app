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
public record ChoreDuenessResult(DueState DueState, ColorTier ColorTier, DateTime? NextDueAt);

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
    DateTime? LastCompletedAt)
{
    /// <summary>Project a <see cref="Chore"/> entity into a recurrence snapshot.</summary>
    public static ChoreRecurrenceSnapshot FromChore(Chore chore) => new(
        chore.RecurrenceMode,
        chore.IntervalDays,
        chore.AnchorDate,
        chore.DaysOfWeek,
        chore.DayOfMonth,
        chore.LastCompletedAt);
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
        var nextDueAt = ComputeNextDueAt(chore, now, tz);

        return chore.RecurrenceMode switch
        {
            RecurrenceMode.Flexible => ComputeFlexible(chore, now, tz, nextDueAt),
            RecurrenceMode.Fixed => ComputeFixed(chore, now, tz, nextDueAt),
            RecurrenceMode.OneOff => ComputeOneOff(chore, now, tz, nextDueAt),
            _ => new ChoreDuenessResult(DueState.NotDue, ColorTier.Fresh, nextDueAt)
        };
    }

    /// <summary>
    /// The next moment (UTC, anchored to local-midnight of the next due local date) the chore comes due.
    /// Shared with WP-04 so the cadence-advance logic lives in one place. Returns null when the chore has
    /// no future cadence point (e.g. a satisfied one-off, or an unsupported / under-specified recurrence).
    /// </summary>
    public DateTime? ComputeNextDueAt(ChoreRecurrenceSnapshot chore, DateTime now, TimeZoneInfo tz)
    {
        var today = LocalDate(now, tz);

        return chore.RecurrenceMode switch
        {
            RecurrenceMode.Flexible => NextDueForFlexible(chore, today, tz),
            RecurrenceMode.Fixed => NextDueForFixed(chore, today, tz),
            RecurrenceMode.OneOff => NextDueForOneOff(chore, tz),
            _ => null
        };
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

    // ---- Fixed: interval-from-anchor OR weekly-on-weekday --------------------------------------------

    private static ChoreDuenessResult ComputeFixed(
        ChoreRecurrenceSnapshot chore, DateTime now, TimeZoneInfo tz, DateTime? nextDueAt)
    {
        var today = LocalDate(now, tz);

        // Weekly-on-weekday (D4-B) — driven by the DaysOfWeek flags.
        if (chore.DaysOfWeek is { } days && days != ChoreDaysOfWeek.None)
        {
            var dueToday = days.HasFlag(ToChoreFlag(today.DayOfWeek));
            return dueToday
                ? new ChoreDuenessResult(DueState.DueToday, ColorTier.Due, nextDueAt)
                : new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, nextDueAt);
        }

        // Interval-from-anchor (every-N days). Needs both an anchor and a positive interval. Dueness is the
        // "on-cadence-day vs not" binary (D4): today lands on a cadence multiple => DueToday; otherwise the
        // chore is simply waiting for its next slot => Scheduled. (Per-completion "missed a slot => overdue"
        // tracking is not modeled for fixed chores in v1; flexible recurrence carries the decay/overdue model.)
        if (chore.AnchorDate is { } anchor && chore.IntervalDays is { } interval && interval > 0)
        {
            var landsOnCadenceDay = today >= anchor &&
                                    (today.DayNumber - anchor.DayNumber) % interval == 0;

            return landsOnCadenceDay
                ? new ChoreDuenessResult(DueState.DueToday, ColorTier.Due, nextDueAt)
                : new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, nextDueAt);
        }

        // Under-specified fixed chore (no DaysOfWeek, no anchor+interval) => benign Scheduled.
        return new ChoreDuenessResult(DueState.Scheduled, ColorTier.Fresh, nextDueAt);
    }

    private static DateTime? NextDueForFixed(ChoreRecurrenceSnapshot chore, DateOnly today, TimeZoneInfo tz)
    {
        // Weekly-on-weekday: next local date (today inclusive) that matches one of the flagged weekdays.
        if (chore.DaysOfWeek is { } days && days != ChoreDaysOfWeek.None)
        {
            for (var offset = 0; offset < 7; offset++)
            {
                var candidate = today.AddDays(offset);
                if (days.HasFlag(ToChoreFlag(candidate.DayOfWeek)))
                {
                    return LocalMidnightUtc(candidate, tz);
                }
            }

            return null; // unreachable: a non-None flag set always matches within 7 days.
        }

        // Interval-from-anchor: the first cadence multiple >= today.
        if (chore.AnchorDate is { } anchor && chore.IntervalDays is { } interval && interval > 0)
        {
            var dueDate = FixedDueOnOrAfter(anchor, interval, today);
            return LocalMidnightUtc(dueDate, tz);
        }

        return null;
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

        // No due date => never overdue, no NextDueAt advance.
        if (chore.AnchorDate is not { } due)
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

        return chore.AnchorDate is { } due ? LocalMidnightUtc(due, tz) : null;
    }

    // ---- Date helpers (all day-boundary math is timezone-aware) --------------------------------------

    /// <summary>The local calendar date of a UTC instant in the injected timezone.</summary>
    private static DateOnly LocalDate(DateTime utc, TimeZoneInfo tz)
    {
        var asUtc = DateTime.SpecifyKind(utc, DateTimeKind.Utc);
        var local = TimeZoneInfo.ConvertTimeFromUtc(asUtc, tz);
        return DateOnly.FromDateTime(local);
    }

    /// <summary>The UTC instant corresponding to local-midnight (00:00) of the given local date.</summary>
    private static DateTime LocalMidnightUtc(DateOnly localDate, TimeZoneInfo tz)
    {
        var localMidnight = localDate.ToDateTime(TimeOnly.MinValue, DateTimeKind.Unspecified);
        return TimeZoneInfo.ConvertTimeToUtc(localMidnight, tz);
    }

    /// <summary>The first cadence date (anchor + k·interval) on or after <paramref name="reference"/>.</summary>
    private static DateOnly FixedDueOnOrAfter(DateOnly anchor, int interval, DateOnly reference)
    {
        var delta = reference.DayNumber - anchor.DayNumber;
        if (delta <= 0)
        {
            return anchor;
        }

        var k = (delta + interval - 1) / interval; // ceil toward the future
        return anchor.AddDays(k * interval);
    }

    /// <summary>Map a <see cref="System.DayOfWeek"/> to the project's custom <see cref="ChoreDaysOfWeek"/> flag.</summary>
    private static ChoreDaysOfWeek ToChoreFlag(DayOfWeek day) => day switch
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
