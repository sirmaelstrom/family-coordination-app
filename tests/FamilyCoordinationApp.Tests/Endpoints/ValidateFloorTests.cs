using FamilyCoordinationApp.Endpoints;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Endpoints;

/// <summary>
/// Pure unit tests for the create/update next-due FLOOR validator
/// <see cref="ChoresEndpoints.ValidateFloor"/> — no DB, no <c>WebApplicationFactory</c>. The create
/// ("first due") and edit ("next due") write paths must apply the SAME "must be in the future" rule the
/// quick-snooze endpoint enforces via <see cref="ChoresEndpoints.ResolveSnooze"/>, so a non-future floor the
/// PATCH path rejects can no longer be persisted unvalidated through create/update (council finding).
/// </summary>
public class ValidateFloorTests
{
    private static readonly DateOnly Today = new(2026, 6, 15);

    [Fact]
    public void Null_IsOk_NoFloor()
    {
        var (ok, error) = ChoresEndpoints.ValidateFloor(floor: null, Today);

        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void FutureDate_IsOk()
    {
        var (ok, error) = ChoresEndpoints.ValidateFloor(floor: new DateOnly(2026, 6, 16), Today);

        ok.Should().BeTrue();
        error.Should().BeNull();
    }

    [Fact]
    public void Today_IsRejected_MustBeStrictlyFuture()
    {
        var (ok, error) = ChoresEndpoints.ValidateFloor(floor: Today, Today);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public void PastDate_IsRejected()
    {
        var (ok, error) = ChoresEndpoints.ValidateFloor(floor: new DateOnly(2026, 6, 10), Today);

        ok.Should().BeFalse();
        error.Should().NotBeNullOrWhiteSpace();
    }
}
