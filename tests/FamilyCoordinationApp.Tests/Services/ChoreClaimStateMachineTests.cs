using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Exercises the claim state machine (WP-04): every legal transition's post-state asserting the atomic
/// assignment triple <c>(AssigneeUserId, AssignmentKind, ClaimedAt)</c> (council M1), every ILLEGAL transition
/// rejected (MN8), the AssignmentKind auto-release guard (stale Claimed releases; Assigned never does), and
/// the lazy AutoReleased event (actor = lapsed claimer, council M16).
/// </summary>
public class ChoreClaimStateMachineTests : IDisposable
{
    private const int HouseholdId = 1;
    private const int OtherHouseholdId = 2;
    private const int Alice = 1;
    private const int Bob = 2;
    private const int Carol = 3;        // member of the OTHER household
    private const int ChoreId = 1;

    // Deterministic "now". Claims made at NowBase; staleness threshold is 48h (WP-02).
    private static readonly DateTime NowBase = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _seedContext;
    private readonly FixedTimeProvider _clock = new(NowBase);
    private readonly ChoreService _service;

    public ChoreClaimStateMachineTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _seedContext = new ApplicationDbContext(_options);

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        _service = new ChoreService(
            dbFactoryMock.Object,
            new ChoreStatusCalculator(),
            Mock.Of<IImageService>(),
            _clock,
            new Mock<ILogger<ChoreService>>().Object);

        SeedBaseline();
    }

    private void SeedBaseline()
    {
        _seedContext.Households.AddRange(
            new Household { Id = HouseholdId, Name = "Smith" },
            new Household { Id = OtherHouseholdId, Name = "Jones" });

        _seedContext.Users.AddRange(
            new User { Id = Alice, HouseholdId = HouseholdId, Email = "a@x.com", DisplayName = "Alice" },
            new User { Id = Bob, HouseholdId = HouseholdId, Email = "b@x.com", DisplayName = "Bob" },
            new User { Id = Carol, HouseholdId = OtherHouseholdId, Email = "c@x.com", DisplayName = "Carol" });

        _seedContext.SaveChanges();
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        GC.SuppressFinalize(this);
    }

    // ---- seed helpers -------------------------------------------------------

    /// <summary>Seeds an Active recurring (Flexible/7d) chore on the pile and returns it.</summary>
    private Chore SeedPileChore()
    {
        var chore = new Chore
        {
            HouseholdId = HouseholdId,
            ChoreId = ChoreId,
            Name = "Dishes",
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            EffortTier = EffortTier.Standard,
            EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            CreatedAt = NowBase.AddDays(-30),
            AssigneeUserId = null,
            AssignmentKind = AssignmentKind.None,
            ClaimedAt = null
        };
        _seedContext.Chores.Add(chore);
        _seedContext.SaveChanges();
        return chore;
    }

    private void SeedChoreWithHold(int assigneeUserId, AssignmentKind kind, DateTime claimedAt)
    {
        _seedContext.Chores.Add(new Chore
        {
            HouseholdId = HouseholdId,
            ChoreId = ChoreId,
            Name = "Dishes",
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            EffortTier = EffortTier.Standard,
            EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            CreatedAt = NowBase.AddDays(-30),
            AssigneeUserId = assigneeUserId,
            AssignmentKind = kind,
            ClaimedAt = claimedAt
        });
        _seedContext.SaveChanges();
    }

    private async Task<Chore> ReloadAsync()
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.Chores.FirstAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == ChoreId);
    }

    private async Task<List<ChoreEvent>> EventsAsync()
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ChoreEvents
            .Where(e => e.HouseholdId == HouseholdId && e.ChoreId == ChoreId)
            .OrderBy(e => e.EventId)
            .ToListAsync();
    }

    // ---- LEGAL transitions --------------------------------------------------

    [Fact]
    public async Task Claim_FromPile_SetsAtomicClaimedTriple_AndLogsClaimedEvent()
    {
        var chore = SeedPileChore();

        var result = await _service.ClaimAsync(HouseholdId, ChoreId, Alice, chore.Version);

        result.AssigneeUserId.Should().Be(Alice);
        result.AssignmentKind.Should().Be(AssignmentKind.Claimed);
        result.ClaimedAt.Should().Be(NowBase);

        var reloaded = await ReloadAsync();
        reloaded.AssigneeUserId.Should().Be(Alice);
        reloaded.AssignmentKind.Should().Be(AssignmentKind.Claimed);
        reloaded.ClaimedAt.Should().Be(NowBase);

        var events = await EventsAsync();
        events.Should().ContainSingle(e => e.Type == ChoreEventType.Claimed)
            .Which.ActorUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task Drop_OwnClaim_ReturnsToPile_AllThreeCleared_AndLogsDropped()
    {
        SeedChoreWithHold(Alice, AssignmentKind.Claimed, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        var result = await _service.DropAsync(HouseholdId, ChoreId, Alice, chore.Version);

        result.AssigneeUserId.Should().BeNull();
        result.AssignmentKind.Should().Be(AssignmentKind.None);
        result.ClaimedAt.Should().BeNull();

        var events = await EventsAsync();
        events.Should().ContainSingle(e => e.Type == ChoreEventType.Dropped)
            .Which.ActorUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task HandOff_ToHouseholdMember_AssignsThem_AndLogsHandedOffWithTarget()
    {
        SeedChoreWithHold(Alice, AssignmentKind.Claimed, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        var result = await _service.HandOffAsync(HouseholdId, ChoreId, Alice, Bob, chore.Version);

        result.AssigneeUserId.Should().Be(Bob);
        result.AssignmentKind.Should().Be(AssignmentKind.Assigned);
        result.ClaimedAt.Should().Be(NowBase);

        var events = await EventsAsync();
        var handoff = events.Should().ContainSingle(e => e.Type == ChoreEventType.HandedOff).Which;
        handoff.ActorUserId.Should().Be(Alice);
        handoff.TargetUserId.Should().Be(Bob);
    }

    [Fact]
    public async Task Take_AChoreFreshlyClaimedByAnother_BecomesSelfClaim_NoCoordinationNeeded()
    {
        // "Take it": Bob holds a FRESH (non-stale) self-claim; Alice takes it (covering). The chore becomes a
        // self-CLAIM by Alice — NOT a sticky Assigned — displacing Bob with no coordination. A Claimed event
        // records Alice. (Completion separately credits whoever marks it done — council M8.)
        SeedChoreWithHold(Bob, AssignmentKind.Claimed, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        var result = await _service.TakeAsync(HouseholdId, ChoreId, Alice, chore.Version);

        result.AssigneeUserId.Should().Be(Alice);
        result.AssignmentKind.Should().Be(AssignmentKind.Claimed);   // claim (returns to pile on complete), not a sticky assignment
        result.ClaimedAt.Should().Be(NowBase);

        var reloaded = await ReloadAsync();
        reloaded.AssigneeUserId.Should().Be(Alice);
        reloaded.AssignmentKind.Should().Be(AssignmentKind.Claimed);

        var events = await EventsAsync();
        events.Should().ContainSingle(e => e.Type == ChoreEventType.Claimed)
            .Which.ActorUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task Take_AnAssignedChore_DisplacesTheAssignee_AsSelfClaim()
    {
        // Bob is deliberately ASSIGNED (it's "his" chore) but is out sick. Alice takes it: the chore is
        // displaced to a self-CLAIM by Alice — so after she completes a recurring instance it returns to the
        // pile rather than staying assigned. Take is allowed to displace an Assigned hold (unlike Claim/Drop).
        SeedChoreWithHold(Bob, AssignmentKind.Assigned, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        var result = await _service.TakeAsync(HouseholdId, ChoreId, Alice, chore.Version);

        result.AssigneeUserId.Should().Be(Alice);
        result.AssignmentKind.Should().Be(AssignmentKind.Claimed);

        var events = await EventsAsync();
        events.Should().ContainSingle(e => e.Type == ChoreEventType.Claimed)
            .Which.ActorUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task Take_AStaleClaimByAnother_AutoReleasesThenSelfClaims()
    {
        // Bob's claim is stale (49h). Alice takes it: the stale claim auto-releases first (actor = the lapsed
        // claimer Bob, M16), then Alice's self-claim lands — events in that order.
        SeedChoreWithHold(Bob, AssignmentKind.Claimed, NowBase.AddHours(-49));
        var chore = await ReloadAsync();

        var result = await _service.TakeAsync(HouseholdId, ChoreId, Alice, chore.Version);

        result.AssigneeUserId.Should().Be(Alice);
        result.AssignmentKind.Should().Be(AssignmentKind.Claimed);

        var events = await EventsAsync();
        events.Select(e => e.Type).Should().ContainInOrder(ChoreEventType.AutoReleased, ChoreEventType.Claimed);
        events.Single(e => e.Type == ChoreEventType.AutoReleased).ActorUserId.Should().Be(Bob);
        events.Single(e => e.Type == ChoreEventType.Claimed).ActorUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task HandOff_ToNull_ReturnsAssignedChoreToPile_TheStuckAssignedEscapeHatch()
    {
        // A deliberately-Assigned chore (cannot be dropped) — hand-off-to-null is its escape hatch (council M9).
        SeedChoreWithHold(Bob, AssignmentKind.Assigned, NowBase.AddDays(-5));
        var chore = await ReloadAsync();

        var result = await _service.HandOffAsync(HouseholdId, ChoreId, Alice, targetUserId: null, chore.Version);

        result.AssigneeUserId.Should().BeNull();
        result.AssignmentKind.Should().Be(AssignmentKind.None);
        result.ClaimedAt.Should().BeNull();

        var events = await EventsAsync();
        var handoff = events.Should().ContainSingle(e => e.Type == ChoreEventType.HandedOff).Which;
        handoff.ActorUserId.Should().Be(Alice);
        handoff.TargetUserId.Should().BeNull();
    }

    [Fact]
    public async Task Claim_AfterStaleClaimByAnother_AutoReleasesThenClaims()
    {
        // Bob's claim is stale (49h old). Alice claims: the stale claim is auto-released first, then Alice claims.
        SeedChoreWithHold(Bob, AssignmentKind.Claimed, NowBase.AddHours(-49));
        var chore = await ReloadAsync();

        var result = await _service.ClaimAsync(HouseholdId, ChoreId, Alice, chore.Version);

        result.AssigneeUserId.Should().Be(Alice);
        result.AssignmentKind.Should().Be(AssignmentKind.Claimed);
        result.ClaimedAt.Should().Be(NowBase);

        var events = await EventsAsync();
        events.Select(e => e.Type).Should().ContainInOrder(ChoreEventType.AutoReleased, ChoreEventType.Claimed);
        events.Single(e => e.Type == ChoreEventType.AutoReleased).ActorUserId.Should().Be(Bob); // lapsed claimer (M16)
        events.Single(e => e.Type == ChoreEventType.Claimed).ActorUserId.Should().Be(Alice);
    }

    // ---- AUTO-RELEASE GUARD (primary risk) ---------------------------------

    [Fact]
    public async Task AutoRelease_StaleClaimed_IsReleasedToPile_WithLapsedClaimerAsActor()
    {
        // Trigger materialization via a drop attempt by a third party: the stale claim auto-releases first
        // (then the drop is rejected because after release nobody holds it — but the release is what we assert).
        SeedChoreWithHold(Bob, AssignmentKind.Claimed, NowBase.AddHours(-49));
        var chore = await ReloadAsync();

        // Hand-off-to-null is the cleanest trigger that always succeeds; it materializes the auto-release, then
        // returns to pile. After both, the chore is on the pile and an AutoReleased event exists.
        await _service.HandOffAsync(HouseholdId, ChoreId, Alice, targetUserId: null, chore.Version);

        var reloaded = await ReloadAsync();
        reloaded.AssigneeUserId.Should().BeNull();
        reloaded.AssignmentKind.Should().Be(AssignmentKind.None);
        reloaded.ClaimedAt.Should().BeNull();

        var events = await EventsAsync();
        var autoRelease = events.Should().ContainSingle(e => e.Type == ChoreEventType.AutoReleased).Which;
        autoRelease.ActorUserId.Should().Be(Bob);   // the LAPSED claimer (council M16)
        autoRelease.TargetUserId.Should().BeNull();
    }

    [Fact]
    public async Task AutoRelease_StaleAssigned_IsNeverReleased_TheAssignmentKindGuard()
    {
        // Bob is deliberately ASSIGNED, claim timestamp 5 days old (> 48h). This must NEVER auto-release —
        // assignment is durable; only self-claims lapse (the D6 correctness guard / primary risk).
        SeedChoreWithHold(Bob, AssignmentKind.Assigned, NowBase.AddDays(-5));
        var chore = await ReloadAsync();

        // Drive a mutation that runs the auto-release pass: hand-off to the same assignee leaves it Assigned,
        // but we specifically assert NO AutoReleased event fired and the assignment survived.
        await _service.HandOffAsync(HouseholdId, ChoreId, Alice, Bob, chore.Version);

        var reloaded = await ReloadAsync();
        reloaded.AssigneeUserId.Should().Be(Bob);
        reloaded.AssignmentKind.Should().Be(AssignmentKind.Assigned);

        var events = await EventsAsync();
        events.Should().NotContain(e => e.Type == ChoreEventType.AutoReleased);
    }

    [Fact]
    public async Task AutoRelease_ClaimExactlyAtThreshold_IsNotStale_NotReleased()
    {
        // Exactly 48h is NOT stale (WP-02: strict >). Claim by Bob; Alice's claim attempt must be rejected
        // (still held), proving no auto-release happened at the boundary.
        SeedChoreWithHold(Bob, AssignmentKind.Claimed, NowBase - ChoreStatusCalculator.StalenessThreshold);
        var chore = await ReloadAsync();

        var act = async () => await _service.ClaimAsync(HouseholdId, ChoreId, Alice, chore.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();

        var events = await EventsAsync();
        events.Should().NotContain(e => e.Type == ChoreEventType.AutoReleased);
    }

    // ---- ILLEGAL transitions (MN8 — must be rejected, never coerced) -------

    [Fact]
    public async Task Claim_AlreadyClaimedByAnother_IsRejected()
    {
        SeedChoreWithHold(Bob, AssignmentKind.Claimed, NowBase.AddHours(-1)); // fresh, not stale
        var chore = await ReloadAsync();

        var act = async () => await _service.ClaimAsync(HouseholdId, ChoreId, Alice, chore.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();

        var reloaded = await ReloadAsync();
        reloaded.AssigneeUserId.Should().Be(Bob);   // unchanged — not silently overwritten
        reloaded.AssignmentKind.Should().Be(AssignmentKind.Claimed);
    }

    [Fact]
    public async Task Claim_AlreadyAssigned_IsRejected()
    {
        SeedChoreWithHold(Bob, AssignmentKind.Assigned, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        var act = async () => await _service.ClaimAsync(HouseholdId, ChoreId, Alice, chore.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Drop_NotHolder_IsRejected()
    {
        SeedChoreWithHold(Bob, AssignmentKind.Claimed, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        var act = async () => await _service.DropAsync(HouseholdId, ChoreId, Alice, chore.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();

        var reloaded = await ReloadAsync();
        reloaded.AssigneeUserId.Should().Be(Bob);   // Bob still holds it
    }

    [Fact]
    public async Task Drop_AnAssignedChore_IsRejected_DropIsClaimedOnly()
    {
        // Even the assignee themselves cannot DROP a deliberately-Assigned chore — drop is Claimed-only
        // (council M9). The escape hatch is hand-off-to-null.
        SeedChoreWithHold(Bob, AssignmentKind.Assigned, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        var act = async () => await _service.DropAsync(HouseholdId, ChoreId, Bob, chore.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();

        var reloaded = await ReloadAsync();
        reloaded.AssignmentKind.Should().Be(AssignmentKind.Assigned);
        reloaded.AssigneeUserId.Should().Be(Bob);
    }

    [Fact]
    public async Task Drop_FromPile_IsRejected()
    {
        var chore = SeedPileChore();

        var act = async () => await _service.DropAsync(HouseholdId, ChoreId, Alice, chore.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task HandOff_ToUserOutsideHousehold_IsRejected()
    {
        SeedChoreWithHold(Alice, AssignmentKind.Claimed, NowBase.AddHours(-1));
        var chore = await ReloadAsync();

        // Carol belongs to the OTHER household — handing off to her must be rejected (M1 isolation, MN8).
        var act = async () => await _service.HandOffAsync(HouseholdId, ChoreId, Alice, Carol, chore.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();

        var reloaded = await ReloadAsync();
        reloaded.AssigneeUserId.Should().Be(Alice);  // unchanged
    }

    [Fact]
    public async Task Mutation_OnMissingChore_ThrowsNotFound()
    {
        var act = async () => await _service.ClaimAsync(HouseholdId, choreId: 999, Alice, version: 0);

        await act.Should().ThrowAsync<ChoreNotFoundException>();
    }

    [Fact]
    public async Task Claim_DoesNotCrossHouseholds()
    {
        // A chore with the same ChoreId exists only in HouseholdId=1. The other household can't see it.
        SeedPileChore();

        var act = async () => await _service.ClaimAsync(OtherHouseholdId, ChoreId, Carol, version: 0);

        await act.Should().ThrowAsync<ChoreNotFoundException>();
    }
}
