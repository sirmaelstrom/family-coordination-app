using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Unit tests (InMemory) for the multi-person roster write-path LOGIC (WP-04, rework): assign/commit/leave
/// event-writing + rejections, the create-time roster seed, and the X=1↔X&gt;1 transition. The real two-writer
/// xmin serialization (V3) is verified against Postgres in WP-05 — the InMemory provider never raises
/// <see cref="DbUpdateConcurrencyException"/>, so concurrency is NOT faked here.
/// </summary>
public class ChoreRosterServiceTests : IDisposable
{
    private const int HouseholdId = 1;
    private const int OtherHouseholdId = 2;
    private const int Alice = 1;
    private const int Bob = 2;
    private const int Carol = 3;   // other household

    private static readonly DateTime NowBase = new(2026, 6, 1, 12, 0, 0, DateTimeKind.Utc);

    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ApplicationDbContext _seedContext;
    private readonly FixedTimeProvider _clock = new(NowBase);
    private readonly ChoreService _service;

    public ChoreRosterServiceTests()
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

    private void SeedChore(int choreId, int requiredCount, int? assigneeUserId = null,
        AssignmentKind kind = AssignmentKind.None, int enteredBy = Alice, int? ownerUserId = null)
    {
        _seedContext.Chores.Add(new Chore
        {
            HouseholdId = HouseholdId,
            ChoreId = choreId,
            Name = "Test chore",
            RecurrenceMode = RecurrenceMode.OneOff,
            EffortTier = EffortTier.Standard,
            EffortPoints = 2,
            RequiredCount = requiredCount,
            Status = ChoreStatus.Active,
            EnteredByUserId = enteredBy,
            OwnerUserId = ownerUserId,
            AssigneeUserId = assigneeUserId,
            AssignmentKind = kind,
            ClaimedAt = assigneeUserId is null ? null : NowBase,
            CreatedAt = NowBase
        });
        _seedContext.SaveChanges();
    }

    private async Task<List<ChoreParticipationEvent>> EventsAsync(int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ChoreParticipationEvents.Where(e => e.ChoreId == choreId).ToListAsync();
    }

    private async Task<Chore> ReloadAsync(int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.Chores.SingleAsync(c => c.ChoreId == choreId);
    }

    // ---- assign / commit / leave -----------------------------------------------------

    [Fact]
    public async Task AssignToRoster_SinglePerson_Throws()
    {
        SeedChore(1, requiredCount: 1);
        var act = () => _service.AssignToRosterAsync(HouseholdId, 1, Alice, Bob, version: 0);
        await act.Should().ThrowAsync<ChoreValidationException>().WithMessage("*single-person*");
    }

    [Fact]
    public async Task AssignToRoster_MultiPerson_WritesAssignedEvent()
    {
        SeedChore(1, requiredCount: 2);
        await _service.AssignToRosterAsync(HouseholdId, 1, actorUserId: Alice, subjectUserId: Bob, version: 0);

        var events = await EventsAsync(1);
        events.Should().ContainSingle();
        events[0].Type.Should().Be(ChoreParticipationType.Assigned);
        events[0].SubjectUserId.Should().Be(Bob);
        events[0].ActorUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task AssignToRoster_NonMemberSubject_Throws()
    {
        SeedChore(1, requiredCount: 2);
        var act = () => _service.AssignToRosterAsync(HouseholdId, 1, Alice, Carol, version: 0);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task CommitToRoster_WritesCommittedEvent()
    {
        SeedChore(1, requiredCount: 2);
        await _service.CommitToRosterAsync(HouseholdId, 1, actorUserId: Bob, version: 0);

        var events = await EventsAsync(1);
        events.Should().ContainSingle();
        events[0].Type.Should().Be(ChoreParticipationType.Committed);
        events[0].SubjectUserId.Should().Be(Bob);
    }

    [Fact]
    public async Task LeaveRoster_Self_WritesLeftEvent()
    {
        SeedChore(1, requiredCount: 2);
        await _service.LeaveRosterAsync(HouseholdId, 1, actorUserId: Bob, subjectUserId: null, version: 0);

        var events = await EventsAsync(1);
        events.Should().ContainSingle();
        events[0].Type.Should().Be(ChoreParticipationType.Left);
        events[0].SubjectUserId.Should().Be(Bob);
    }

    [Fact]
    public async Task LeaveRoster_RemoveOther_WithoutAuthority_Throws()
    {
        SeedChore(1, requiredCount: 2, enteredBy: Alice, ownerUserId: null);
        // Bob is neither creator nor owner — cannot remove Alice.
        var act = () => _service.LeaveRosterAsync(HouseholdId, 1, actorUserId: Bob, subjectUserId: Alice, version: 0);
        await act.Should().ThrowAsync<ChoreValidationException>().WithMessage("*creator or owner*");
    }

    [Fact]
    public async Task LeaveRoster_RemoveOther_AsCreator_Writes()
    {
        SeedChore(1, requiredCount: 2, enteredBy: Alice);
        await _service.LeaveRosterAsync(HouseholdId, 1, actorUserId: Alice, subjectUserId: Bob, version: 0);

        var events = await EventsAsync(1);
        events.Should().ContainSingle();
        events[0].SubjectUserId.Should().Be(Bob);
        events[0].Type.Should().Be(ChoreParticipationType.Left);
    }

    // ---- create-time roster seed -----------------------------------------------------

    [Fact]
    public async Task CreateChore_MultiPerson_SeedsRoster_AndLeavesTrioOnPile()
    {
        var cmd = new CreateChoreCommand(
            Name: "Flip the mattress", Description: null,
            RecurrenceMode: RecurrenceMode.OneOff, IntervalDays: null, AnchorDate: new DateOnly(2026, 6, 10),
            DaysOfWeek: null, DayOfMonth: null, EffortTier: EffortTier.BigJob,
            OwnerUserId: null, AssigneeUserId: null, PhotoPath: null,
            RequiredCount: 2, AssignedUserIds: new[] { Alice, Bob });

        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, cmd);

        chore.AssignmentKind.Should().Be(AssignmentKind.None);
        chore.AssigneeUserId.Should().BeNull();

        var events = await EventsAsync(chore.ChoreId);
        events.Should().HaveCount(2);
        events.Should().OnlyContain(e => e.Type == ChoreParticipationType.Assigned);
        events.Select(e => e.SubjectUserId).Should().BeEquivalentTo(new[] { Alice, Bob });
    }

    [Fact]
    public async Task CreateChore_SinglePerson_IgnoresAssignedUserIds()
    {
        var cmd = new CreateChoreCommand(
            Name: "Dishes", Description: null,
            RecurrenceMode: RecurrenceMode.OneOff, IntervalDays: null, AnchorDate: new DateOnly(2026, 6, 10),
            DaysOfWeek: null, DayOfMonth: null, EffortTier: EffortTier.Quick,
            OwnerUserId: null, AssigneeUserId: Bob, PhotoPath: null,
            RequiredCount: 1, AssignedUserIds: new[] { Alice });

        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, cmd);

        chore.AssignmentKind.Should().Be(AssignmentKind.Assigned);
        chore.AssigneeUserId.Should().Be(Bob);
        (await EventsAsync(chore.ChoreId)).Should().BeEmpty();
    }

    // ---- X=1 ↔ X>1 transition (D9) ---------------------------------------------------

    [Fact]
    public async Task UpdateChore_OneToMany_ConvertsAssigneeToRoster_AndClearsTrio()
    {
        SeedChore(1, requiredCount: 1, assigneeUserId: Bob, kind: AssignmentKind.Assigned);
        var cmd = new UpdateChoreCommand(
            Name: "Test chore", Description: null,
            RecurrenceMode: RecurrenceMode.OneOff, IntervalDays: null, AnchorDate: new DateOnly(2026, 6, 10),
            DaysOfWeek: null, DayOfMonth: null, EffortTier: EffortTier.Standard,
            OwnerUserId: null, PhotoPath: null, RequiredCount: 2);

        var chore = await _service.UpdateChoreAsync(HouseholdId, 1, cmd, version: 0);

        chore.AssignmentKind.Should().Be(AssignmentKind.None);
        chore.AssigneeUserId.Should().BeNull();

        var events = await EventsAsync(1);
        events.Should().ContainSingle();
        events[0].Type.Should().Be(ChoreParticipationType.Assigned);
        events[0].SubjectUserId.Should().Be(Bob);
    }

    [Fact]
    public async Task UpdateChore_ManyToOne_ClearsToPile()
    {
        SeedChore(1, requiredCount: 2);
        var cmd = new UpdateChoreCommand(
            Name: "Test chore", Description: null,
            RecurrenceMode: RecurrenceMode.OneOff, IntervalDays: null, AnchorDate: new DateOnly(2026, 6, 10),
            DaysOfWeek: null, DayOfMonth: null, EffortTier: EffortTier.Standard,
            OwnerUserId: null, PhotoPath: null, RequiredCount: 1);

        var chore = await _service.UpdateChoreAsync(HouseholdId, 1, cmd, version: 0);

        chore.RequiredCount.Should().Be(1);
        chore.AssignmentKind.Should().Be(AssignmentKind.None);
        chore.AssigneeUserId.Should().BeNull();
    }
}
