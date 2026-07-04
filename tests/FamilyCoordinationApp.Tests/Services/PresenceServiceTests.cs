using FamilyCoordinationApp.Services;
using FluentAssertions;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests for <see cref="PresenceService"/> — the in-memory presence tracker the de-Blazored shell's
/// <c>GET /api/presence/users</c> endpoint (WP-02) reads. That endpoint calls
/// <see cref="PresenceService.UpdatePresence"/> itself before <see cref="PresenceService.GetAllActiveUsers"/>,
/// because WP-12 deletes the <c>PollingService</c> that used to drive the Online→Away→Offline staleness decay
/// on a timer. These tests lock that decay contract so a user who closes their tab ages out of the roster
/// instead of showing Online forever (the CRITICAL failure WP-02 calls out) — and lock the household scoping
/// of the roster (multi-tenant boundary: presence is a cross-household singleton dictionary).
/// </summary>
public class PresenceServiceTests
{
    [Fact]
    public void Heartbeat_MarksUserOnline_AndListsThem()
    {
        var svc = new PresenceService();
        svc.Heartbeat(1, householdId: 10, "Alice", pictureUrl: null, initials: "AL", currentPage: "/shopping-list");

        var active = svc.GetAllActiveUsers(10).ToList();
        active.Should().ContainSingle(u => u.UserId == 1);
        active.Single(u => u.UserId == 1).Status.Should().Be(PresenceStatus.Online);
    }

    [Fact]
    public void GetAllActiveUsers_ReturnsEveryOnlineUser_TheEndpointExcludesTheCaller()
    {
        var svc = new PresenceService();
        svc.Heartbeat(1, 10, "Alice", null, "AL");
        svc.Heartbeat(2, 10, "Bob", null, "BO");

        // The service returns both; the /users endpoint filters out the caller (Where UserId != caller.UserId).
        svc.GetAllActiveUsers(10).Select(u => u.UserId).Should().BeEquivalentTo(new[] { 1, 2 });
    }

    [Fact]
    public void GetAllActiveUsers_IsHouseholdScoped_NoCrossTenantLeak()
    {
        var svc = new PresenceService();
        svc.Heartbeat(1, 10, "Alice", null, "AL");
        svc.Heartbeat(2, 20, "Mallory", null, "MA");

        // Multi-tenant boundary: household 10's roster must never contain household 20's users.
        svc.GetAllActiveUsers(10).Select(u => u.UserId).Should().BeEquivalentTo(new[] { 1 });
        svc.GetAllActiveUsers(20).Select(u => u.UserId).Should().BeEquivalentTo(new[] { 2 });
    }

    [Fact]
    public void Heartbeat_ReheartbeatKeepsHouseholdCurrent()
    {
        var svc = new PresenceService();
        svc.Heartbeat(1, 10, "Alice", null, "AL");

        // A user moved to another household must not linger in the old household's roster.
        svc.Heartbeat(1, 30, "Alice", null, "AL");

        svc.GetAllActiveUsers(10).Should().BeEmpty();
        svc.GetAllActiveUsers(30).Select(u => u.UserId).Should().BeEquivalentTo(new[] { 1 });
    }

    [Fact]
    public void UpdatePresence_AgesAStaleUserToOffline_SoTheyDropFromTheRoster()
    {
        var svc = new PresenceService();
        svc.Heartbeat(1, 10, "Alice", null, "AL");

        // Simulate a closed tab: last seen 16 minutes ago (> the 15-min Offline threshold).
        svc.GetUserPresence(1)!.LastSeen = DateTime.UtcNow.AddMinutes(-16);

        svc.UpdatePresence();

        svc.GetUserPresence(1)!.Status.Should().Be(PresenceStatus.Offline);
        svc.GetAllActiveUsers(10).Should().NotContain(u => u.UserId == 1);
    }

    [Fact]
    public void UpdatePresence_AgesAnIdleUserToAway_ButKeepsThemInTheRoster()
    {
        var svc = new PresenceService();
        svc.Heartbeat(1, 10, "Alice", null, "AL");

        // Idle 6 minutes (> the 5-min Away threshold, < the 15-min Offline threshold).
        svc.GetUserPresence(1)!.LastSeen = DateTime.UtcNow.AddMinutes(-6);

        svc.UpdatePresence();

        var alice = svc.GetAllActiveUsers(10).SingleOrDefault(u => u.UserId == 1);
        alice.Should().NotBeNull();
        alice!.Status.Should().Be(PresenceStatus.Away);
    }
}
