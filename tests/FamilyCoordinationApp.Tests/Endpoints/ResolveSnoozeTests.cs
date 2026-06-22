using FamilyCoordinationApp.Endpoints;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Endpoints;

/// <summary>
/// Pure unit tests for the snooze-request validator <see cref="ChoresEndpoints.ResolveSnooze"/> (V9, P3) — no
/// DB, no <c>WebApplicationFactory</c>. Pins the three valid cases (days / explicit future date / clear) and the
/// rejections (both supplied, days &lt; 1, a date not strictly in the future) against a fixed tz-resolved
/// <c>today</c>. The server resolving the floor (never the client) is the MN4 invariant.
/// </summary>
public class ResolveSnoozeTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Fact]
    public void Days_PositiveAndNoUntil_ResolvesToTodayPlusDays()
    {
        var (ok, until, error) = ChoresEndpoints.ResolveSnooze(days: 3, until: null, Today);

        ok.Should().BeTrue();
        until.Should().Be(new DateOnly(2026, 6, 18));
        error.Should().BeNull();
    }

    [Fact]
    public void Until_FutureDateAndNoDays_ResolvesToThatDate()
    {
        var (ok, until, error) = ChoresEndpoints.ResolveSnooze(days: null, until: new DateOnly(2026, 6, 20), Today);

        ok.Should().BeTrue();
        until.Should().Be(new DateOnly(2026, 6, 20));
        error.Should().BeNull();
    }

    [Fact]
    public void BothNull_ClearsTheFloor_NotAnError()
    {
        var (ok, until, error) = ChoresEndpoints.ResolveSnooze(days: null, until: null, Today);

        ok.Should().BeTrue();
        until.Should().BeNull();   // un-snooze
        error.Should().BeNull();
    }

    [Fact]
    public void DaysAndUntilTogether_Rejected_AsAmbiguous()
    {
        var (ok, until, error) = ChoresEndpoints.ResolveSnooze(days: 3, until: new DateOnly(2026, 6, 20), Today);

        ok.Should().BeFalse();
        until.Should().BeNull();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void DaysLessThanOne_Rejected(int days)
    {
        var (ok, _, error) = ChoresEndpoints.ResolveSnooze(days, until: null, Today);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UntilToday_Rejected_MustBeStrictlyFuture()
    {
        var (ok, _, error) = ChoresEndpoints.ResolveSnooze(days: null, until: Today, Today);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void UntilInThePast_Rejected()
    {
        var (ok, _, error) = ChoresEndpoints.ResolveSnooze(days: null, until: new DateOnly(2026, 6, 10), Today);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
