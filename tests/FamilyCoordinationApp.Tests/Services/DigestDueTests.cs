using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Digest;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// PURE unit tests for the digest due-determination helpers (<see cref="DigestDue.IsDue"/>,
/// <see cref="DigestDue.SendWindowStartUtc"/>) and the shared <see cref="ChoreAttention"/> predicates.
/// Every test injects an explicit UTC <c>now</c> and an explicit <see cref="TimeZoneInfo"/> — fully
/// deterministic, no DbContext.
/// <para>
/// ⚠ The full <c>DigestService.RunDueAsync</c> orchestration is NOT exercised here: it issues an atomic
/// <c>ExecuteUpdateAsync</c> claim, which EF InMemory does not support (it throws). The claim/idempotency/
/// failure-isolation behavior is integration-tested in WP-08 against real Postgres.
/// </para>
/// </summary>
public class DigestDueTests
{
    // America/Chicago: CST (UTC-6) winter, CDT (UTC-5) summer. Spring-forward 2026 is 2026-03-08 02:00→03:00.
    private static readonly TimeZoneInfo Chicago = ResolveTz("America/Chicago", "Central Standard Time");

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

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    // ---------------------------------------------------------------- SendWindowStartUtc

    [Fact]
    public void SendWindowStartUtc_ReturnsLocalSendHour_AsUtc_StandardTime()
    {
        // 2026-05-31 is summer (CDT, UTC-5). Local 18:00 Chicago == 23:00 UTC.
        var now = Utc(2026, 5, 31, 23, 30); // local 18:30 Chicago

        var windowStart = DigestDue.SendWindowStartUtc(sendHour: 18, now, Chicago);

        windowStart.Should().Be(Utc(2026, 5, 31, 23, 0));
        windowStart.Kind.Should().Be(DateTimeKind.Utc);
    }

    [Fact]
    public void SendWindowStartUtc_UsesLocalCalendarDate_NotUtcDate()
    {
        // Local Sunday 18:00 window, but UTC instant has already rolled into Monday. The window must anchor
        // to the LOCAL date (Sunday), not the UTC date — proves local-tz day-boundary math (M5).
        // 2026-05-31 (Sun) local 19:00 Chicago (CDT) == 2026-06-01 00:00 UTC.
        var now = Utc(2026, 6, 1, 0, 0);

        var windowStart = DigestDue.SendWindowStartUtc(sendHour: 18, now, Chicago);

        // Local Sunday 18:00 CDT == 2026-05-31 23:00 UTC.
        windowStart.Should().Be(Utc(2026, 5, 31, 23, 0));
    }

    [Fact]
    public void SendWindowStartUtc_DstSpringForwardGap_PushesPastMissingHour_DoesNotThrow()
    {
        // 2026-03-08 in Chicago: 02:00→03:00 spring-forward. Local 02:00 does NOT exist. A household
        // configured to send at 02:00 local would otherwise make ConvertTimeToUtc THROW. The guard pushes
        // to 03:00 local. now = 2026-03-08 09:00 UTC == local 03:00 CDT (post-jump).
        var now = Utc(2026, 3, 8, 9, 0);

        Action act = () => DigestDue.SendWindowStartUtc(sendHour: 2, now, Chicago);
        act.Should().NotThrow();

        var windowStart = DigestDue.SendWindowStartUtc(sendHour: 2, now, Chicago);
        // Pushed to 03:00 local CDT (UTC-5) == 08:00 UTC.
        windowStart.Should().Be(Utc(2026, 3, 8, 8, 0));
    }

    // ---------------------------------------------------------------- IsDue

    [Fact]
    public void IsDue_True_WhenRightWeekday_HourReached_NeverSent()
    {
        // Sunday 2026-05-31 local 18:30 Chicago (CDT) == 23:30 UTC.
        var now = Utc(2026, 5, 31, 23, 30);

        DigestDue.IsDue(DayOfWeek.Sunday, sendHour: 18, lastSentAt: null, now, Chicago).Should().BeTrue();
    }

    [Fact]
    public void IsDue_False_WhenWrongWeekday()
    {
        // Saturday 2026-05-30 local 18:30 Chicago == 23:30 UTC. Configured for Sunday.
        var now = Utc(2026, 5, 30, 23, 30);

        DigestDue.IsDue(DayOfWeek.Sunday, sendHour: 18, lastSentAt: null, now, Chicago).Should().BeFalse();
    }

    [Fact]
    public void IsDue_False_WhenBeforeSendHour()
    {
        // Sunday local 17:30 Chicago (CDT) == 22:30 UTC — before the 18:00 send hour.
        var now = Utc(2026, 5, 31, 22, 30);

        DigestDue.IsDue(DayOfWeek.Sunday, sendHour: 18, lastSentAt: null, now, Chicago).Should().BeFalse();
    }

    [Fact]
    public void IsDue_True_ExactlyAtSendHour()
    {
        // Sunday local 18:00 Chicago (CDT) == 23:00 UTC. Hour boundary is inclusive (>=).
        var now = Utc(2026, 5, 31, 23, 0);

        DigestDue.IsDue(DayOfWeek.Sunday, sendHour: 18, lastSentAt: null, now, Chicago).Should().BeTrue();
    }

    [Fact]
    public void IsDue_False_WhenAlreadySentThisWindow()
    {
        var now = Utc(2026, 5, 31, 23, 30); // Sunday local 18:30
        var alreadySent = Utc(2026, 5, 31, 23, 5); // sent at local 18:05 today (>= window start)

        DigestDue.IsDue(DayOfWeek.Sunday, sendHour: 18, lastSentAt: alreadySent, now, Chicago).Should().BeFalse();
    }

    [Fact]
    public void IsDue_True_WhenLastSentWasBeforeThisWindow()
    {
        var now = Utc(2026, 5, 31, 23, 30); // Sunday local 18:30
        var lastWeek = Utc(2026, 5, 24, 23, 0); // sent the prior Sunday

        DigestDue.IsDue(DayOfWeek.Sunday, sendHour: 18, lastSentAt: lastWeek, now, Chicago).Should().BeTrue();
    }

    [Fact]
    public void IsDue_False_AtExactWindowStart_WhenLastSentEqualsWindowStart()
    {
        // LastSentAt == windowStart is NOT strictly before it => not due (the < guard, idempotent E10).
        var now = Utc(2026, 5, 31, 23, 30); // Sunday local 18:30
        var windowStart = Utc(2026, 5, 31, 23, 0); // == today's window start

        DigestDue.IsDue(DayOfWeek.Sunday, sendHour: 18, lastSentAt: windowStart, now, Chicago).Should().BeFalse();
    }

    // ---------------------------------------------------------------- ChoreAttention truth tables

    [Theory]
    [InlineData(DueState.Overdue, true)]
    [InlineData(DueState.DueToday, true)]
    [InlineData(DueState.NotDue, false)]
    [InlineData(DueState.Scheduled, false)]
    public void IsFallingBehind_TruthTable(DueState state, bool expected)
    {
        ChoreAttention.IsFallingBehind(state).Should().Be(expected);
    }

    [Theory]
    // None => up-for-grabs regardless of staleness.
    [InlineData(AssignmentKind.None, false, true)]
    [InlineData(AssignmentKind.None, true, true)]
    // Assigned => never up-for-grabs (assignment is durable; IsClaimStale is false for Assigned anyway).
    [InlineData(AssignmentKind.Assigned, false, false)]
    // Claimed => up-for-grabs only when the claim has gone stale.
    [InlineData(AssignmentKind.Claimed, false, false)]
    [InlineData(AssignmentKind.Claimed, true, true)]
    public void IsUpForGrabs_TruthTable(AssignmentKind kind, bool isClaimStale, bool expected)
    {
        ChoreAttention.IsUpForGrabs(kind, isClaimStale).Should().Be(expected);
    }
}
