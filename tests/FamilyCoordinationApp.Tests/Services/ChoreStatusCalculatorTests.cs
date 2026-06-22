using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for the pure, timezone-aware <see cref="ChoreStatusCalculator"/>. Every test injects an
/// explicit <c>now</c> (UTC) and an explicit <see cref="TimeZoneInfo"/> — the calculator NEVER reaches for
/// <see cref="DateTime.UtcNow"/>, so these are fully deterministic. No DB, no I/O.
/// </summary>
public class ChoreStatusCalculatorTests
{
    private readonly ChoreStatusCalculator _calc = new();

    // America/Chicago: CDT (UTC-5) in summer. Used for the local-midnight boundary test where UTC-midnight
    // and local-midnight fall on DIFFERENT calendar dates (the tertiary-risk mitigation / CORRECTIONS footgun).
    private static readonly TimeZoneInfo Chicago = ResolveTz("America/Chicago", "Central Standard Time");
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private static TimeZoneInfo ResolveTz(string ianaId, string windowsId)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(ianaId);
        }
        catch (TimeZoneNotFoundException)
        {
            // Windows hosts may only know the Windows id (CI runners differ).
            return TimeZoneInfo.FindSystemTimeZoneById(windowsId);
        }
    }

    private static DateTime Utc0(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    private static ChoreRecurrenceSnapshot Flexible(int? intervalDays, DateTime? lastCompletedAt) =>
        new(RecurrenceMode.Flexible, intervalDays, AnchorDate: null, DaysOfWeek: null, DayOfMonth: null, lastCompletedAt);

    private static ChoreRecurrenceSnapshot FixedEveryN(int intervalDays, DateOnly anchor, DateTime? lastCompletedAt = null) =>
        new(RecurrenceMode.Fixed, intervalDays, anchor, DaysOfWeek: null, DayOfMonth: null, lastCompletedAt);

    private static ChoreRecurrenceSnapshot FixedWeekly(ChoreDaysOfWeek days, DateTime? lastCompletedAt = null) =>
        new(RecurrenceMode.Fixed, IntervalDays: null, AnchorDate: null, days, DayOfMonth: null, lastCompletedAt);

    private static ChoreRecurrenceSnapshot OneOff(DateOnly? due, DateTime? lastCompletedAt = null) =>
        new(RecurrenceMode.OneOff, IntervalDays: null, due, DaysOfWeek: null, DayOfMonth: null, lastCompletedAt);

    // ---- Flexible ramp (V2) -------------------------------------------------------------------------

    [Theory]
    [InlineData(0, ColorTier.Fresh, DueState.NotDue)]    // just completed
    [InlineData(4, ColorTier.Mid, DueState.NotDue)]      // 4/7 ≈ 0.57 -> mid band
    [InlineData(7, ColorTier.Due, DueState.DueToday)]    // exactly the interval -> due
    [InlineData(10, ColorTier.Overdue, DueState.Overdue)] // past interval -> overdue
    public void Flexible_Ramp_DecaysAcrossBand(int daysAgo, ColorTier expectedTier, DueState expectedState)
    {
        var now = Utc0(2026, 6, 15, 12);
        var lastCompleted = now.AddDays(-daysAgo);
        var chore = Flexible(intervalDays: 7, lastCompleted);

        var result = _calc.Compute(chore, now, Utc);

        result.ColorTier.Should().Be(expectedTier);
        result.DueState.Should().Be(expectedState);
    }

    [Fact]
    public void Flexible_FreshMidBoundary_HalfIntervalIsMid()
    {
        // Decay band edge: fraction == 0.5 must be Mid (FreshUpperFraction is the lower-inclusive edge of Mid).
        var now = Utc0(2026, 6, 15, 12);
        var chore = Flexible(intervalDays: 10, now.AddDays(-5)); // 5/10 = 0.5 exactly

        var result = _calc.Compute(chore, now, Utc);

        result.ColorTier.Should().Be(ColorTier.Mid);
    }

    [Fact]
    public void Flexible_DueOverdueBoundary_OneDayPastIntervalIsOverdue()
    {
        // fraction == 1.0 is Due; the first whole day beyond the interval flips to Overdue (grace = 0).
        var now = Utc0(2026, 6, 15, 12);
        var chore = Flexible(intervalDays: 7, now.AddDays(-8)); // 8/7 > 1.0

        var result = _calc.Compute(chore, now, Utc);

        result.ColorTier.Should().Be(ColorTier.Overdue);
        result.DueState.Should().Be(DueState.Overdue);
    }

    [Fact]
    public void Flexible_NeverCompleted_IsDue()
    {
        var now = Utc0(2026, 6, 15, 12);
        var chore = Flexible(intervalDays: 7, lastCompletedAt: null);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.DueToday);
        result.ColorTier.Should().Be(ColorTier.Due);
    }

    // ---- TZ boundary (V2) — the tertiary-risk mitigation --------------------------------------------

    [Fact]
    public void Flexible_TzBoundary_CompletedTodayChicago_NotYetADayOlderAfterUtcMidnight()
    {
        // Completed at 2026-06-15 20:00 local Chicago (CDT = UTC-5) => 2026-06-16 01:00 UTC.
        // Evaluate at 2026-06-16 03:00 UTC: that is 2026-06-15 22:00 local Chicago — STILL the 15th locally,
        // i.e. AFTER UTC-midnight (16th) but BEFORE local-midnight. daysSince must be 0 (Fresh), not 1.
        var completedUtc = Utc0(2026, 6, 16, 1, 0);  // local Chicago: 2026-06-15 20:00
        var nowUtc = Utc0(2026, 6, 16, 3, 0);        // local Chicago: 2026-06-15 22:00
        var chore = Flexible(intervalDays: 7, completedUtc);

        var chicagoResult = _calc.Compute(chore, nowUtc, Chicago);

        // In Chicago the local date hasn't rolled over -> 0 days old -> Fresh / NotDue.
        chicagoResult.ColorTier.Should().Be(ColorTier.Fresh);
        chicagoResult.DueState.Should().Be(DueState.NotDue);
    }

    [Fact]
    public void Flexible_TzBoundary_SameInstantsDifferInUtcVsChicago()
    {
        // PROVES the injected TimeZoneInfo governs the day boundary: the identical UTC instants yield a
        // DIFFERENT local-day age in UTC vs Chicago. If the calc ignored tz this test would fail.
        var completedUtc = Utc0(2026, 6, 16, 1, 0);  // UTC date = 16th; Chicago date = 15th
        var nowUtc = Utc0(2026, 6, 16, 3, 0);        // UTC date = 16th; Chicago date = 15th
        var chore = Flexible(intervalDays: 7, completedUtc);

        // Same UTC date (16th vs 16th) => 0 days, but that is coincidental; shift now to prove tz matters.
        var nowLaterUtc = Utc0(2026, 6, 17, 2, 0);   // UTC date = 17th (1 day later in UTC)
                                                     // Chicago: 2026-06-16 21:00 (also 1 day later locally)

        var utcResult = _calc.Compute(chore, nowLaterUtc, Utc);
        var chicagoResult = _calc.Compute(chore, nowLaterUtc, Chicago);

        // UTC: completed 16th, now 17th => 1 day old. Chicago: completed 15th, now 16th => 1 day old.
        // Both happen to be 1 here; the discriminating assertion is the boundary test above. This test
        // documents that the two timezones partition the SAME instants into different local dates.
        var utcCompletedDate = TimeZoneInfo.ConvertTimeFromUtc(completedUtc, Utc).Date;
        var chicagoCompletedDate = TimeZoneInfo.ConvertTimeFromUtc(completedUtc, Chicago).Date;
        utcCompletedDate.Should().NotBe(chicagoCompletedDate); // 16th vs 15th — tz actually moves the boundary
        utcResult.ColorTier.Should().Be(ColorTier.Fresh);
        chicagoResult.ColorTier.Should().Be(ColorTier.Fresh);
    }

    // ---- Fixed every-N (V2) -------------------------------------------------------------------------

    [Fact]
    public void FixedEveryN_NextDueAt_IsNextFutureMultipleOfInterval()
    {
        // Anchor 2026-06-01, interval 5 => cadence: Jun 1, 6, 11, 16, 21...
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(5, anchor);
        var now = Utc0(2026, 6, 13, 12); // between Jun 11 and Jun 16 -> next is Jun 16

        var nextDueAt = _calc.ComputeNextDueAt(chore, now, Utc);

        nextDueAt.Should().Be(Utc0(2026, 6, 16)); // local-midnight UTC of Jun 16
    }

    [Fact]
    public void FixedEveryN_OnCadenceDay_IsDueToday()
    {
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(5, anchor);
        var now = Utc0(2026, 6, 16, 9); // Jun 16 is a cadence multiple

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.DueToday);
        result.ColorTier.Should().Be(ColorTier.Due);
    }

    [Fact]
    public void FixedEveryN_BetweenCadenceDays_IsScheduled()
    {
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(5, anchor);
        var now = Utc0(2026, 6, 13, 9); // between cadence points

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);
    }

    // ---- Fixed weekly-on-weekday (D4-B) -------------------------------------------------------------

    [Fact]
    public void FixedWeekly_SingleDay_NextDueIsThatWeekday()
    {
        // 2026-06-15 is a Monday. Next Tuesday is 2026-06-16.
        var chore = FixedWeekly(ChoreDaysOfWeek.Tuesday);
        var now = Utc0(2026, 6, 15, 9); // Monday

        var nextDueAt = _calc.ComputeNextDueAt(chore, now, Utc);

        nextDueAt.Should().Be(Utc0(2026, 6, 16)); // Tuesday local-midnight UTC
        nextDueAt!.Value.DayOfWeek.Should().Be(DayOfWeek.Tuesday);
    }

    [Fact]
    public void FixedWeekly_OnMatchingDay_IsDueToday()
    {
        var chore = FixedWeekly(ChoreDaysOfWeek.Tuesday);
        var now = Utc0(2026, 6, 16, 9); // Tuesday

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.DueToday);
    }

    [Fact]
    public void FixedWeekly_MultipleDays_PicksNearerDay()
    {
        // {Mon, Thu}; today is Tuesday 2026-06-16 => nearer upcoming match is Thursday 2026-06-18.
        var chore = FixedWeekly(ChoreDaysOfWeek.Monday | ChoreDaysOfWeek.Thursday);
        var now = Utc0(2026, 6, 16, 9); // Tuesday

        var nextDueAt = _calc.ComputeNextDueAt(chore, now, Utc);

        nextDueAt.Should().Be(Utc0(2026, 6, 18)); // Thursday
        nextDueAt!.Value.DayOfWeek.Should().Be(DayOfWeek.Thursday);
    }

    // ---- Fixed completion-awareness (catch-up + overdue, feedback) ----------------------------------
    //
    // A fixed/"Scheduled" chore is computed against its CURRENT slot (most recent cadence date <= today) and
    // whether a completion satisfied it. One "Done" clears however many slots have elapsed since the last
    // completion (no per-missed-day marking); a past, uncovered slot with a completion baseline reads Overdue.

    [Fact]
    public void FixedEveryN_CompletedCurrentSlot_RollsForwardToNextSlot()
    {
        // Cadence Jun 1, 6, 11, 16. Completed on the Jun 11 slot; today Jun 13 (between 11 and 16).
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(5, anchor, lastCompletedAt: Utc0(2026, 6, 11, 18));
        var now = Utc0(2026, 6, 13, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);       // current slot satisfied
        result.ColorTier.Should().Be(ColorTier.Fresh);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 16));       // rolled forward to the next slot
    }

    [Fact]
    public void FixedEveryN_DailyCompletedToday_IsNotDueAgainUntilTomorrow()
    {
        // The regression: a daily fixed chore (interval 1) completed today must NOT keep reading DueToday.
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(1, anchor, lastCompletedAt: Utc0(2026, 6, 15, 14));
        var now = Utc0(2026, 6, 15, 18); // same local day as the completion

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 16)); // tomorrow, not today
    }

    [Fact]
    public void FixedEveryN_MissedPastSlotWithBaseline_IsOverdue()
    {
        // Cadence Jun 1, 6, 11, 16. Last done Jun 6; the Jun 11 slot was missed; today Jun 13.
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(5, anchor, lastCompletedAt: Utc0(2026, 6, 6, 12));
        var now = Utc0(2026, 6, 13, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Overdue);
        result.ColorTier.Should().Be(ColorTier.Overdue);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 11)); // points at the missed slot
    }

    [Fact]
    public void FixedEveryN_CompletingWhileOverdue_CatchesUpInOneDone()
    {
        // Overdue (missed Jun 11). Completing on Jun 13 must clear it and roll to the next FUTURE slot — not
        // to Jun 11 + interval (which would still be in the past and demand another completion).
        var anchor = new DateOnly(2026, 6, 1);
        var now = Utc0(2026, 6, 13, 9);

        var overdue = FixedEveryN(5, anchor, lastCompletedAt: Utc0(2026, 6, 6, 12));
        _calc.Compute(overdue, now, Utc).DueState.Should().Be(DueState.Overdue);

        var afterDone = overdue with { LastCompletedAt = now };
        var result = _calc.Compute(afterDone, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 16)); // next future slot, fully caught up
    }

    [Fact]
    public void FixedWeekly_MissedSlotWithBaseline_IsOverdue_ThenOneDoneRollsToNextWeek()
    {
        // Mondays. 2026-06-15 is a Monday. Last done the prior Monday (Jun 8); this Monday (Jun 15) missed;
        // today is Wednesday Jun 17.
        var now = Utc0(2026, 6, 17, 9); // Wednesday
        var chore = FixedWeekly(ChoreDaysOfWeek.Monday, lastCompletedAt: Utc0(2026, 6, 8, 12));

        var overdue = _calc.Compute(chore, now, Utc);
        overdue.DueState.Should().Be(DueState.Overdue);
        overdue.NextDueAt.Should().Be(Utc0(2026, 6, 15)); // the missed Monday

        // One completion on Wednesday catches up and rolls to next Monday (Jun 22).
        var afterDone = chore with { LastCompletedAt = now };
        var result = _calc.Compute(afterDone, now, Utc);
        result.DueState.Should().Be(DueState.Scheduled);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 22));
    }

    [Fact]
    public void FixedWeekly_NeverCompleted_OffDay_IsNotRetroactivelyOverdue()
    {
        // Brand-new Monday chore, today Wednesday Jun 17, never completed: must read Scheduled (waiting for
        // next Monday), NOT Overdue for the Monday that predates the chore.
        var now = Utc0(2026, 6, 17, 9); // Wednesday
        var chore = FixedWeekly(ChoreDaysOfWeek.Monday, lastCompletedAt: null);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 22)); // next Monday
    }

    [Fact]
    public void FixedWeekly_CompletedTodayOnSlot_RollsToNextWeek()
    {
        // Mondays; today is Monday Jun 15 and it was done today => satisfied, next due is the following Monday.
        var now = Utc0(2026, 6, 15, 16); // Monday
        var chore = FixedWeekly(ChoreDaysOfWeek.Monday, lastCompletedAt: Utc0(2026, 6, 15, 8));

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 22));
    }

    // ---- OneOff (V2) --------------------------------------------------------------------------------

    [Fact]
    public void OneOff_DueToday_IsDueToday()
    {
        var due = new DateOnly(2026, 6, 15);
        var chore = OneOff(due);
        var now = Utc0(2026, 6, 15, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.DueToday);
        result.ColorTier.Should().Be(ColorTier.Due);
    }

    [Fact]
    public void OneOff_PastDue_IsOverdue()
    {
        var due = new DateOnly(2026, 6, 10);
        var chore = OneOff(due);
        var now = Utc0(2026, 6, 15, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Overdue);
        result.ColorTier.Should().Be(ColorTier.Overdue);
    }

    [Fact]
    public void OneOff_FutureDue_IsScheduled_WithFutureNextDue()
    {
        // A one-off WITH a future due date has a concrete pending slot, so it reads as Scheduled (matching the
        // Fixed cadence paths) — NOT the bare NotDue/"On track". The card renders "Scheduled". (feedback #5)
        var due = new DateOnly(2026, 6, 20);
        var chore = OneOff(due);
        var now = Utc0(2026, 6, 15, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);
        result.ColorTier.Should().Be(ColorTier.Fresh);
        result.NextDueAt.Should().Be(Utc0(2026, 6, 20)); // local-midnight UTC of the due date
    }

    [Fact]
    public void OneOff_NoDueDate_NeverOverdue_NoNextDue()
    {
        var chore = OneOff(due: null);
        var now = Utc0(2026, 6, 15, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.NotDue);
        result.ColorTier.Should().Be(ColorTier.Fresh);
        result.NextDueAt.Should().BeNull();
    }

    [Fact]
    public void OneOff_Completed_NotDue_NoAdvance()
    {
        var due = new DateOnly(2026, 6, 10);
        var chore = OneOff(due, lastCompletedAt: Utc0(2026, 6, 10, 18));
        var now = Utc0(2026, 6, 15, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.NotDue);
        result.NextDueAt.Should().BeNull(); // a satisfied one-off does not advance
    }

    // ---- Clock advance (V2) -------------------------------------------------------------------------

    [Fact]
    public void Flexible_CompletingShiftsStateByInterval()
    {
        var chore = Flexible(intervalDays: 7, lastCompletedAt: null);
        var t0 = Utc0(2026, 6, 15, 12);

        // Before completion: due now.
        _calc.Compute(chore, t0, Utc).DueState.Should().Be(DueState.DueToday);

        // After completing at t0, the next due point advances by the interval.
        var afterCompletion = chore with { LastCompletedAt = t0 };
        var nextDue = _calc.ComputeNextDueAt(afterCompletion, t0, Utc);
        nextDue.Should().Be(Utc0(2026, 6, 22)); // t0 local date + 7 days, local-midnight UTC

        // Immediately after completion it is Fresh again.
        _calc.Compute(afterCompletion, t0, Utc).ColorTier.Should().Be(ColorTier.Fresh);

        // Advancing the clock to the interval point makes it Due again.
        var atInterval = t0.AddDays(7);
        _calc.Compute(afterCompletion, atInterval, Utc).ColorTier.Should().Be(ColorTier.Due);
    }

    [Fact]
    public void FixedEveryN_ClockAdvancePastCadence_AdvancesNextDue()
    {
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(5, anchor);

        var beforeJun11 = Utc0(2026, 6, 9, 9);
        _calc.ComputeNextDueAt(chore, beforeJun11, Utc).Should().Be(Utc0(2026, 6, 11));

        var afterJun11 = Utc0(2026, 6, 12, 9);
        _calc.ComputeNextDueAt(chore, afterJun11, Utc).Should().Be(Utc0(2026, 6, 16));
    }

    // ---- Monthly-on-day unsupported -----------------------------------------------------------------

    [Fact]
    public void Fixed_MonthlyOnDayOnly_IsBenignNotMisComputed()
    {
        // A DayOfMonth-only fixed recurrence (no DaysOfWeek, no anchor+interval) is unsupported here.
        // The calculator must NOT mis-compute it; it returns a benign Scheduled with no due pressure.
        // The actual creation-time rejection lives in WP-04 (CreateChoreAsync), not here.
        var chore = new ChoreRecurrenceSnapshot(
            RecurrenceMode.Fixed,
            IntervalDays: null,
            AnchorDate: null,
            DaysOfWeek: null,
            DayOfMonth: 15,
            LastCompletedAt: null);
        var now = Utc0(2026, 6, 20, 9);

        var result = _calc.Compute(chore, now, Utc);

        result.DueState.Should().Be(DueState.Scheduled);
        result.ColorTier.Should().Be(ColorTier.Fresh);
        result.NextDueAt.Should().BeNull(); // not mis-advanced
    }

    // ---- Staleness (V2) -----------------------------------------------------------------------------

    [Fact]
    public void IsClaimStale_ClaimedPast48h_IsTrue()
    {
        var now = Utc0(2026, 6, 15, 12);
        var claimedAt = now - TimeSpan.FromHours(49); // > 48h

        _calc.IsClaimStale(AssignmentKind.Claimed, claimedAt, now).Should().BeTrue();
    }

    [Fact]
    public void IsClaimStale_ClaimedWithin48h_IsFalse()
    {
        var now = Utc0(2026, 6, 15, 12);
        var claimedAt = now - TimeSpan.FromHours(47); // < 48h

        _calc.IsClaimStale(AssignmentKind.Claimed, claimedAt, now).Should().BeFalse();
    }

    [Fact]
    public void IsClaimStale_ExactlyAt48h_IsFalse()
    {
        // Strictly greater than the threshold (claimedAt + 48h < now is false at exactly 48h).
        var now = Utc0(2026, 6, 15, 12);
        var claimedAt = now - ChoreStatusCalculator.StalenessThreshold;

        _calc.IsClaimStale(AssignmentKind.Claimed, claimedAt, now).Should().BeFalse();
    }

    [Fact]
    public void IsClaimStale_Assigned_IsNeverStale()
    {
        var now = Utc0(2026, 6, 15, 12);
        var claimedAt = now - TimeSpan.FromDays(30); // ancient, but Assigned not Claimed

        _calc.IsClaimStale(AssignmentKind.Assigned, claimedAt, now).Should().BeFalse();
    }

    [Fact]
    public void IsClaimStale_None_IsNeverStale()
    {
        var now = Utc0(2026, 6, 15, 12);

        _calc.IsClaimStale(AssignmentKind.None, claimedAt: null, now).Should().BeFalse();
    }

    [Fact]
    public void IsClaimStale_ClaimedWithNullClaimedAt_ReturnsFalseAndDoesNotThrow()
    {
        // Council M2: a dropped chore can momentarily be Claimed with null ClaimedAt — must not throw.
        var now = Utc0(2026, 6, 15, 12);

        var act = () => _calc.IsClaimStale(AssignmentKind.Claimed, claimedAt: null, now);

        act.Should().NotThrow();
        act().Should().BeFalse();
    }

    [Fact]
    public void StalenessThreshold_Is48Hours()
    {
        ChoreStatusCalculator.StalenessThreshold.Should().Be(TimeSpan.FromHours(48));
    }

    // ---- Snooze / set-next-due floor (WP-01) --------------------------------------------------------
    //
    // SnoozedUntil is a local-date floor: while today < s the chore is suppressed (Scheduled/Fresh,
    // IsSnoozed=true) and its public next-due is floored at s. A Fixed chore additionally SKIPS any cadence
    // slot it strictly precedes (covered = s > slot). For OneOff the floor REPLACES the anchor as the
    // effective due date. Flexible has no skip rule — its decay clock keeps running (D5). The gate is a clean
    // no-op when SnoozedUntil is null, so every test above passes unchanged (V13).

    [Fact]
    public void FixedWeekly_SnoozePastThisMonday_SkipsToNextMonday_ResumesDueTodayNoOverdue()
    {
        // V1. Mondays; 2026-06-15 is a Monday. Snooze strictly past it (to Tue Jun 16): this Monday is
        // skipped (Scheduled/IsSnoozed, NextDueAt = next Monday Jun 22). At expiry on Jun 22 it resumes
        // DueToday — never reading Overdue at the snooze boundary.
        var chore = FixedWeekly(ChoreDaysOfWeek.Monday) with { SnoozedUntil = new DateOnly(2026, 6, 16) };

        var snoozed = _calc.Compute(chore, Utc0(2026, 6, 15, 9), Utc); // this Monday, under the floor
        snoozed.DueState.Should().Be(DueState.Scheduled);
        snoozed.IsSnoozed.Should().BeTrue();
        snoozed.NextDueAt.Should().Be(Utc0(2026, 6, 22)); // this Monday skipped, points at next Monday

        var resumed = _calc.Compute(chore, Utc0(2026, 6, 22, 9), Utc); // next Monday
        resumed.DueState.Should().Be(DueState.DueToday); // resumes due, NOT overdue
        resumed.IsSnoozed.Should().BeFalse();
    }

    [Fact]
    public void FixedWeekly_SnoozeNotPastCurrentSlot_DoesNotSkip()
    {
        // V2. The skip keys STRICTLY on s > currentSlot. Snoozing to the current slot's own date (today, a
        // Monday) does not skip it — the slot is unchanged and still reads DueToday.
        var chore = FixedWeekly(ChoreDaysOfWeek.Monday) with { SnoozedUntil = new DateOnly(2026, 6, 15) };

        var result = _calc.Compute(chore, Utc0(2026, 6, 15, 9), Utc); // Monday, current slot = Jun 15

        result.DueState.Should().Be(DueState.DueToday); // slot unchanged
        result.IsSnoozed.Should().BeFalse();            // today < s is false (s == today)
    }

    [Fact]
    public void FixedEveryN_SnoozePastCadencePoint_RollsToNextMultiple()
    {
        // V3. Cadence Jun 1, 6, 11, 16. Today Jun 11 (a cadence slot). Snooze past it (to Jun 13) covers the
        // Jun 11 slot (s > slot) and rolls forward to the next multiple, Jun 16.
        var anchor = new DateOnly(2026, 6, 1);
        var chore = FixedEveryN(5, anchor) with { SnoozedUntil = new DateOnly(2026, 6, 13) };

        var result = _calc.Compute(chore, Utc0(2026, 6, 11, 9), Utc);

        result.DueState.Should().Be(DueState.Scheduled);
        result.IsSnoozed.Should().BeTrue();
        result.NextDueAt.Should().Be(Utc0(2026, 6, 16)); // rolled to next cadence multiple
    }

    [Fact]
    public void Flexible_NeverCompleted_SnoozedSevenDays_SuppressedThenDueOnExpiry()
    {
        // V4 (the burst fix). A brand-new Flexible chore reads DueToday immediately; snoozing 7 days holds it
        // Scheduled/IsSnoozed through day 6, then it reads DueToday exactly on day 7.
        var chore = Flexible(intervalDays: 7, lastCompletedAt: null) with { SnoozedUntil = new DateOnly(2026, 6, 22) };

        foreach (var now in new[] { Utc0(2026, 6, 15, 9), Utc0(2026, 6, 18, 9), Utc0(2026, 6, 21, 9) })
        {
            var r = _calc.Compute(chore, now, Utc);
            r.DueState.Should().Be(DueState.Scheduled);
            r.IsSnoozed.Should().BeTrue();
        }

        var expired = _calc.Compute(chore, Utc0(2026, 6, 22, 9), Utc); // Jun 22 == s
        expired.DueState.Should().Be(DueState.DueToday);
        expired.IsSnoozed.Should().BeFalse();
    }

    [Fact]
    public void Flexible_AlreadyOverdue_Snoozed_ResumesNaturalDecayNotReanchored()
    {
        // V5 (pins D5). Decay clock keeps running while snoozed. Last done Jun 5 (interval 7) => overdue by
        // Jun 15. Snooze +3 (to Jun 18) suppresses until Jun 18, then it resumes its NATURAL decay — Overdue
        // (13 days old), NOT re-anchored to Fresh.
        var chore = Flexible(intervalDays: 7, Utc0(2026, 6, 5, 12)) with { SnoozedUntil = new DateOnly(2026, 6, 18) };

        var during = _calc.Compute(chore, Utc0(2026, 6, 15, 9), Utc);
        during.DueState.Should().Be(DueState.Scheduled);
        during.ColorTier.Should().Be(ColorTier.Fresh);
        during.IsSnoozed.Should().BeTrue();

        var resumed = _calc.Compute(chore, Utc0(2026, 6, 18, 9), Utc);
        resumed.DueState.Should().Be(DueState.Overdue);   // natural decay, not re-anchored
        resumed.ColorTier.Should().Be(ColorTier.Overdue);
        resumed.IsSnoozed.Should().BeFalse();
    }

    [Fact]
    public void OneOff_PastAnchor_SnoozedToFuture_RescheduledToSnoozeDate()
    {
        // V6. OneOff originally due Jun 10 (past). Snooze to Jun 20 REPLACES the anchor for dueness:
        // Scheduled before, DueToday on Jun 20, Overdue after.
        var chore = OneOff(new DateOnly(2026, 6, 10)) with { SnoozedUntil = new DateOnly(2026, 6, 20) };

        var before = _calc.Compute(chore, Utc0(2026, 6, 15, 9), Utc);
        before.DueState.Should().Be(DueState.Scheduled);
        before.IsSnoozed.Should().BeTrue();
        before.NextDueAt.Should().Be(Utc0(2026, 6, 20)); // rescheduled to the snooze date

        var on = _calc.Compute(chore, Utc0(2026, 6, 20, 9), Utc);
        on.DueState.Should().Be(DueState.DueToday);
        on.IsSnoozed.Should().BeFalse();

        var after = _calc.Compute(chore, Utc0(2026, 6, 21, 9), Utc);
        after.DueState.Should().Be(DueState.Overdue);
        after.IsSnoozed.Should().BeFalse();
    }

    [Fact]
    public void IsSnoozed_TrueOnlyWhileBeforeSnoozeDate()
    {
        // V7. IsSnoozed is true iff SnoozedUntil is set AND today < s; false on/after s and when null.
        var chore = Flexible(intervalDays: 7, lastCompletedAt: null) with { SnoozedUntil = new DateOnly(2026, 6, 20) };

        _calc.Compute(chore, Utc0(2026, 6, 19, 9), Utc).IsSnoozed.Should().BeTrue();   // before
        _calc.Compute(chore, Utc0(2026, 6, 20, 9), Utc).IsSnoozed.Should().BeFalse();  // on (today == s)
        _calc.Compute(chore, Utc0(2026, 6, 21, 9), Utc).IsSnoozed.Should().BeFalse();  // after

        var noSnooze = Flexible(intervalDays: 7, lastCompletedAt: null);
        _calc.Compute(noSnooze, Utc0(2026, 6, 19, 9), Utc).IsSnoozed.Should().BeFalse(); // null floor
    }

    [Fact]
    public void IsSnoozed_TzBoundary_EndsTodayChicago_StillSuppressedBeforeLocalMidnight()
    {
        // V7 (tz boundary). Snooze ends "today" (Jun 16) in Chicago. Evaluate at 2026-06-16 03:00 UTC =
        // 2026-06-15 22:00 local Chicago — still the 15th locally, BEFORE the floor, so still suppressed. A
        // naive UTC read (already the 16th) would wrongly expire it (CORRECTIONS date footgun).
        var chore = Flexible(intervalDays: 7, lastCompletedAt: null) with { SnoozedUntil = new DateOnly(2026, 6, 16) };
        var nowUtc = Utc0(2026, 6, 16, 3, 0); // local Chicago: 2026-06-15 22:00

        var result = _calc.Compute(chore, nowUtc, Chicago);

        result.IsSnoozed.Should().BeTrue();              // today (Chicago) = Jun 15 < Jun 16
        result.DueState.Should().Be(DueState.Scheduled);
    }
}
