using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Exercises chore CRUD, creation/recurrence validation, completion (log + clock advance + pile/keep), and
/// the conflict-surfacing MAPPING (WP-04). The real two-writer xmin race is verified against Postgres in
/// WP-08 — the InMemory provider never raises <see cref="DbUpdateConcurrencyException"/>, so the 409
/// DETECTION is NOT faked here; only the typed-exception MAPPING is unit-tested via a context whose save
/// throws.
/// </summary>
public class ChoreServiceTests : IDisposable
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

    public ChoreServiceTests()
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
        // Rooms for the multi-room membership tests (Phase 13). Ids 10/11 exist in household 1.
        _seedContext.Rooms.AddRange(
            new Room { HouseholdId = HouseholdId, RoomId = Kitchen, Name = "Kitchen", Icon = "🍳", SortOrder = 1, CreatedAt = NowBase },
            new Room { HouseholdId = HouseholdId, RoomId = Bathroom, Name = "Bathroom", Icon = "🛁", SortOrder = 2, CreatedAt = NowBase });
        _seedContext.SaveChanges();
    }

    private const int Kitchen = 10;
    private const int Bathroom = 11;

    private async Task<List<int>> MembershipsAsync(int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ChoreRooms
            .Where(cr => cr.HouseholdId == HouseholdId && cr.ChoreId == choreId)
            .OrderBy(cr => cr.RoomId)
            .Select(cr => cr.RoomId)
            .ToListAsync();
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CreateChoreCommand FlexibleCmd(int? assignee = null) => new(
        Name: "Dishes",
        Description: "  rinse first  ",
        RecurrenceMode: RecurrenceMode.Flexible,
        IntervalDays: 7,
        AnchorDate: null,
        DaysOfWeek: null,
        DayOfMonth: null,
        EffortTier: EffortTier.Standard,
        OwnerUserId: null,
        AssigneeUserId: assignee,
        PhotoPath: null,
        Icon: "🧹");

    private async Task<Chore> ReloadAsync(int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.Chores.FirstAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == choreId);
    }

    // ---- CREATE -------------------------------------------------------------

    [Fact]
    public async Task Create_AssignsNextChoreId_TrimsFields_SnapshotsEffortPoints_LogsCreated()
    {
        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());

        chore.ChoreId.Should().Be(1);
        chore.HouseholdId.Should().Be(HouseholdId);
        chore.Name.Should().Be("Dishes");
        chore.Description.Should().Be("rinse first");        // trimmed
        chore.Icon.Should().Be("🧹");
        chore.Status.Should().Be(ChoreStatus.Active);
        chore.EnteredByUserId.Should().Be(Alice);
        chore.EffortPoints.Should().Be(ChoreEffort.PointsFor(EffortTier.Standard));

        // Pile by default (council M1 triple).
        chore.AssigneeUserId.Should().BeNull();
        chore.AssignmentKind.Should().Be(AssignmentKind.None);
        chore.ClaimedAt.Should().BeNull();

        await using var ctx = new ApplicationDbContext(_options);
        (await ctx.ChoreEvents.CountAsync(e => e.ChoreId == 1 && e.Type == ChoreEventType.Created)).Should().Be(1);
    }

    [Fact]
    public async Task Create_WithAssignee_SetsAssignedTriple()
    {
        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd(assignee: Bob));

        chore.AssigneeUserId.Should().Be(Bob);
        chore.AssignmentKind.Should().Be(AssignmentKind.Assigned);
        chore.ClaimedAt.Should().Be(NowBase);
    }

    [Fact]
    public async Task Create_WithAssigneeOutsideHousehold_IsRejected()
    {
        var act = async () => await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd(assignee: Carol));

        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Create_PerHouseholdChoreIdSequence()
    {
        await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        var second = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        second.ChoreId.Should().Be(2);

        // Other household starts its own sequence at 1.
        var otherCmd = FlexibleCmd() with { };
        var other = await _service.CreateChoreAsync(OtherHouseholdId, Carol, otherCmd);
        other.ChoreId.Should().Be(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Create_EmptyName_IsRejected(string name)
    {
        var cmd = FlexibleCmd() with { Name = name };
        var act = async () => await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Create_DayOfMonthOnlyRecurrence_IsRejected_D4B()
    {
        // Monthly-on-day is deferred (D4-B / E5 ceiling): a Fixed chore with only DayOfMonth set is rejected.
        var cmd = FlexibleCmd() with
        {
            RecurrenceMode = RecurrenceMode.Fixed,
            IntervalDays = null,
            DaysOfWeek = null,
            DayOfMonth = 15
        };

        var act = async () => await _service.CreateChoreAsync(HouseholdId, Alice, cmd);

        await act.Should().ThrowAsync<ChoreValidationException>()
            .WithMessage("*DayOfMonth*");
    }

    [Fact]
    public async Task Create_FixedWithNoCadence_IsRejected()
    {
        var cmd = FlexibleCmd() with
        {
            RecurrenceMode = RecurrenceMode.Fixed,
            IntervalDays = null,
            DaysOfWeek = null,
            DayOfMonth = null
        };

        var act = async () => await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Create_FixedWithDaysOfWeek_IsAccepted()
    {
        var cmd = FlexibleCmd() with
        {
            RecurrenceMode = RecurrenceMode.Fixed,
            IntervalDays = null,
            DaysOfWeek = ChoreDaysOfWeek.Monday | ChoreDaysOfWeek.Thursday,
            DayOfMonth = null
        };

        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        chore.DaysOfWeek.Should().Be(ChoreDaysOfWeek.Monday | ChoreDaysOfWeek.Thursday);
    }

    [Fact]
    public async Task Create_OneOff_NeedsNoCadence()
    {
        var cmd = FlexibleCmd() with
        {
            RecurrenceMode = RecurrenceMode.OneOff,
            IntervalDays = null,
            DaysOfWeek = null,
            AnchorDate = new DateOnly(2026, 6, 10)
        };

        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        chore.RecurrenceMode.Should().Be(RecurrenceMode.OneOff);
    }

    // ---- ROOM MEMBERSHIP (Phase 13, WP-02) ---------------------------------

    private static UpdateChoreCommand RoomUpdateCmd(IReadOnlyList<int>? roomIds = null) => new(
        Name: "Dishes",
        Description: null,
        RecurrenceMode: RecurrenceMode.Flexible,
        IntervalDays: 7,
        AnchorDate: null,
        DaysOfWeek: null,
        DayOfMonth: null,
        EffortTier: EffortTier.Standard,
        OwnerUserId: null,
        PhotoPath: null,
        Icon: "🧹",
        RoomIds: roomIds);

    [Fact]
    public async Task Create_WithRoomIds_WritesSortedMembershipRows()
    {
        // Unsorted input → sorted rows.
        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RoomIds = new[] { Bathroom, Kitchen } });

        (await MembershipsAsync(chore.ChoreId)).Should().Equal(Kitchen, Bathroom);
    }

    [Fact]
    public async Task Create_WithDuplicateRoomIds_DedupesToSingleRow()
    {
        // roomIds:[10,10] must not violate the composite PK — the helper .Distinct()-normalizes.
        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RoomIds = new[] { Kitchen, Kitchen } });

        (await MembershipsAsync(chore.ChoreId)).Should().Equal(Kitchen);
    }

    [Fact]
    public async Task Create_WithNoRooms_IsGeneral()
    {
        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());

        (await MembershipsAsync(chore.ChoreId)).Should().BeEmpty();
    }

    [Fact]
    public async Task Create_WithInvalidRoomId_ThrowsValidation()
    {
        var act = async () => await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RoomIds = new[] { Kitchen, 999 } });

        await act.Should().ThrowAsync<ChoreValidationException>()
            .Where(e => e.Message.Contains("999"));
    }

    [Fact]
    public async Task Update_ToSubsetRoomIds_RemovesDeselected()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RoomIds = new[] { Kitchen, Bathroom } });
        var current = await ReloadAsync(created.ChoreId);

        await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, RoomUpdateCmd(roomIds: new[] { Bathroom }), current.Version);

        (await MembershipsAsync(created.ChoreId)).Should().Equal(new[] { Bathroom }, "the deselected room's row is removed");
    }

    [Fact]
    public async Task Update_ToEmptyRoomIds_ClearsToGeneral()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RoomIds = new[] { Kitchen, Bathroom } });
        var current = await ReloadAsync(created.ChoreId);

        await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, RoomUpdateCmd(roomIds: Array.Empty<int>()), current.Version);

        (await MembershipsAsync(created.ChoreId)).Should().BeEmpty("[] clears to General");
    }

    [Fact]
    public async Task Update_WithNullRoomIds_PreservesMemberships()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RoomIds = new[] { Kitchen, Bathroom } });
        var current = await ReloadAsync(created.ChoreId);

        // Neither roomIds nor legacy roomId supplied → transitional no-op (preserve).
        await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, RoomUpdateCmd(), current.Version);

        (await MembershipsAsync(created.ChoreId)).Should().Equal(new[] { Kitchen, Bathroom }, "null roomIds preserves memberships");
    }

    [Fact]
    public async Task Delete_RemovesMembershipRows()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RoomIds = new[] { Kitchen, Bathroom } });
        var current = await ReloadAsync(created.ChoreId);

        await _service.DeleteChoreAsync(HouseholdId, created.ChoreId, current.Version);

        (await MembershipsAsync(created.ChoreId)).Should().BeEmpty("chore-delete removes its membership rows first (M4)");
    }

    // ---- UPDATE / DELETE ----------------------------------------------------

    [Fact]
    public async Task Update_ChangesEditableFields_DoesNotTouchAssignmentTriple()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        await _service.ClaimAsync(HouseholdId, created.ChoreId, Bob, (await ReloadAsync(created.ChoreId)).Version);

        var current = await ReloadAsync(created.ChoreId);
        var updateCmd = new UpdateChoreCommand(
            Name: "Wash dishes",
            Description: null,
            RecurrenceMode: RecurrenceMode.Flexible,
            IntervalDays: 3,
            AnchorDate: null,
            DaysOfWeek: null,
            DayOfMonth: null,
            EffortTier: EffortTier.BigJob,
            OwnerUserId: Alice,
            PhotoPath: null,
            Icon: "🚿");

        var updated = await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, updateCmd, current.Version);

        updated.Name.Should().Be("Wash dishes");
        updated.IntervalDays.Should().Be(3);
        updated.EffortTier.Should().Be(EffortTier.BigJob);
        updated.EffortPoints.Should().Be(ChoreEffort.PointsFor(EffortTier.BigJob));
        updated.OwnerUserId.Should().Be(Alice);
        updated.Icon.Should().Be("🚿");                       // icon is editable (unlike the assignment trio)

        // Assignment triple untouched by an edit (Bob still holds the claim).
        updated.AssigneeUserId.Should().Be(Bob);
        updated.AssignmentKind.Should().Be(AssignmentKind.Claimed);
    }

    [Fact]
    public async Task Update_MissingChore_ThrowsNotFound()
    {
        var cmd = new UpdateChoreCommand("X", null, RecurrenceMode.OneOff, null, null, null, null, EffortTier.Quick, null, null);
        var act = async () => await _service.UpdateChoreAsync(HouseholdId, choreId: 99, cmd, version: 0);
        await act.Should().ThrowAsync<ChoreNotFoundException>();
    }

    [Fact]
    public async Task Delete_RemovesChore()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        var current = await ReloadAsync(created.ChoreId);

        await _service.DeleteChoreAsync(HouseholdId, created.ChoreId, current.Version);

        await using var ctx = new ApplicationDbContext(_options);
        (await ctx.Chores.AnyAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == created.ChoreId)).Should().BeFalse();
    }

    [Fact]
    public async Task Delete_MissingChore_ThrowsNotFound()
    {
        var act = async () => await _service.DeleteChoreAsync(HouseholdId, choreId: 99, version: 0);
        await act.Should().ThrowAsync<ChoreNotFoundException>();
    }

    // ---- COMPLETE -----------------------------------------------------------

    [Fact]
    public async Task Complete_FromPile_Unclaimed_Succeeds_RecordsActorAsCompleter()
    {
        // Council M8: an unclaimed pile chore may be completed directly; the completer is the actor.
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        var current = await ReloadAsync(created.ChoreId);

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Bob, note: " good ", photoPath: null, participantUserIds: null, current.Version);

        result.LastCompletedAt.Should().Be(NowBase);

        await using var ctx = new ApplicationDbContext(_options);
        var completion = await ctx.ChoreCompletions.SingleAsync(cc => cc.ChoreId == created.ChoreId);
        completion.CompletedByUserId.Should().Be(Bob);
        completion.CompletedAt.Should().Be(NowBase);
        completion.EffortPointsSnapshot.Should().Be(ChoreEffort.PointsFor(EffortTier.Standard));
        completion.Note.Should().Be("good");   // trimmed
    }

    [Fact]
    public async Task Complete_ClaimedByBob_ByAlice_RecordsAliceAsCompleter()
    {
        // Council M8: any member may complete; CompletedBy = actor even if a different user held the claim.
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        await _service.ClaimAsync(HouseholdId, created.ChoreId, Bob, (await ReloadAsync(created.ChoreId)).Version);

        var current = await ReloadAsync(created.ChoreId);
        await _service.CompleteAsync(HouseholdId, created.ChoreId, Alice, note: null, photoPath: null, participantUserIds: null, current.Version);

        await using var ctx = new ApplicationDbContext(_options);
        var completion = await ctx.ChoreCompletions.SingleAsync(cc => cc.ChoreId == created.ChoreId);
        completion.CompletedByUserId.Should().Be(Alice);
    }

    [Fact]
    public async Task Complete_RecurringClaimed_ReturnsToPile()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        await _service.ClaimAsync(HouseholdId, created.ChoreId, Bob, (await ReloadAsync(created.ChoreId)).Version);

        var current = await ReloadAsync(created.ChoreId);
        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Bob, null, null, participantUserIds: null, current.Version);

        // Recurring + Claimed => back to pile after completion (all three cleared, council M1).
        result.AssigneeUserId.Should().BeNull();
        result.AssignmentKind.Should().Be(AssignmentKind.None);
        result.ClaimedAt.Should().BeNull();
        result.Status.Should().Be(ChoreStatus.Active);   // recurring stays Active
    }

    [Fact]
    public async Task Complete_RecurringAssigned_KeepsStickyAssignee()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd(assignee: Bob));
        var current = await ReloadAsync(created.ChoreId);

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Bob, null, null, participantUserIds: null, current.Version);

        // Recurring + Assigned => keeps the sticky assignee.
        result.AssigneeUserId.Should().Be(Bob);
        result.AssignmentKind.Should().Be(AssignmentKind.Assigned);
        result.Status.Should().Be(ChoreStatus.Active);
    }

    [Fact]
    public async Task Complete_OneOff_SetsStatusDone()
    {
        var cmd = FlexibleCmd() with
        {
            RecurrenceMode = RecurrenceMode.OneOff,
            IntervalDays = null,
            AnchorDate = new DateOnly(2026, 6, 1)
        };
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        var current = await ReloadAsync(created.ChoreId);

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, current.Version);

        result.Status.Should().Be(ChoreStatus.Done);
        result.LastCompletedAt.Should().Be(NowBase);
    }

    [Fact]
    public async Task Complete_Fixed_DoesNotRewriteAnchorDate()
    {
        var anchor = new DateOnly(2026, 5, 1);
        var cmd = FlexibleCmd() with
        {
            RecurrenceMode = RecurrenceMode.Fixed,
            IntervalDays = 7,
            AnchorDate = anchor,
            DaysOfWeek = null
        };
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        var current = await ReloadAsync(created.ChoreId);

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, current.Version);

        // Fixed completion mutates NO recurrence field beyond LastCompletedAt — the anchor is untouched.
        result.AnchorDate.Should().Be(anchor);
        result.IntervalDays.Should().Be(7);
        result.LastCompletedAt.Should().Be(NowBase);
    }

    [Fact]
    public async Task Complete_MissingChore_ThrowsNotFound()
    {
        var act = async () => await _service.CompleteAsync(HouseholdId, choreId: 99, Alice, null, null, participantUserIds: null, version: 0);
        await act.Should().ThrowAsync<ChoreNotFoundException>();
    }

    // ---- MULTI-PERSON COMPLETE (co-sign gate, D1=C / D4 / D5 / D6 / D7) ------

    /// <summary>Council M1 trio invariant: AssigneeUserId==null ⟺ AssignmentKind==None ⟺ ClaimedAt==null.</summary>
    private static void AssertTrioInvariant(Chore chore)
    {
        var noAssignee = chore.AssigneeUserId is null;
        var kindNone = chore.AssignmentKind == AssignmentKind.None;
        var noClaimedAt = chore.ClaimedAt is null;
        (noAssignee == kindNone).Should().BeTrue("AssigneeUserId==null ⟺ AssignmentKind==None");
        (kindNone == noClaimedAt).Should().BeTrue("AssignmentKind==None ⟺ ClaimedAt==null");
    }

    private async Task<int> CompletionRowCountAsync(int choreId)
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ChoreCompletions.CountAsync(cc => cc.HouseholdId == HouseholdId && cc.ChoreId == choreId);
    }

    private static CreateChoreCommand OneOffCmd(int requiredCount) => FlexibleCmd() with
    {
        RecurrenceMode = RecurrenceMode.OneOff,
        IntervalDays = null,
        DaysOfWeek = null,
        AnchorDate = new DateOnly(2026, 6, 1),
        RequiredCount = requiredCount
    };

    [Fact]
    public async Task Complete_OneOff_RequiredTwo_FirstCompleter_IsPartial_NoAdvance()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, OneOffCmd(requiredCount: 2));
        var current = await ReloadAsync(created.ChoreId);

        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, current.Version);

        // Partial: not satisfied — no advance, no Done, but LastContributionAt stamped + 1 row.
        result.Status.Should().Be(ChoreStatus.Active);
        result.LastCompletedAt.Should().BeNull();
        result.LastContributionAt.Should().Be(NowBase);
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(1);
        AssertTrioInvariant(result);
    }

    [Fact]
    public async Task Complete_OneOff_RequiredTwo_SecondDistinctUser_Satisfies_SetsDone()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, OneOffCmd(requiredCount: 2));

        await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, (await ReloadAsync(created.ChoreId)).Version);

        var afterFirst = await ReloadAsync(created.ChoreId);
        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Bob, null, null, participantUserIds: null, afterFirst.Version);

        // The 2nd distinct contributor satisfies RequiredCount=2 → advance.
        result.Status.Should().Be(ChoreStatus.Done);
        result.LastCompletedAt.Should().Be(NowBase);
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(2);
        AssertTrioInvariant(result);
    }

    [Fact]
    public async Task Complete_Flexible_RequiredTwo_ClaimedByA_PartialByA_LeavesTrioAndClockUntouched()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });
        await _service.ClaimAsync(HouseholdId, created.ChoreId, Alice, (await ReloadAsync(created.ChoreId)).Version);

        var current = await ReloadAsync(created.ChoreId);
        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, current.Version);

        // Partial completion by the holder must NOT advance or change assignment (D4): trio still (A, Claimed).
        result.AssigneeUserId.Should().Be(Alice);
        result.AssignmentKind.Should().Be(AssignmentKind.Claimed);
        result.LastCompletedAt.Should().BeNull();
        result.LastContributionAt.Should().Be(NowBase);
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(1);
        AssertTrioInvariant(result);
    }

    [Fact]
    public async Task Complete_Flexible_RequiredTwo_ClaimedByA_SecondByB_ReturnsToPile()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });
        await _service.ClaimAsync(HouseholdId, created.ChoreId, Alice, (await ReloadAsync(created.ChoreId)).Version);

        await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, (await ReloadAsync(created.ChoreId)).Version);

        var afterFirst = await ReloadAsync(created.ChoreId);
        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Bob, null, null, participantUserIds: null, afterFirst.Version);

        // The satisfying completion of a recurring + Claimed chore returns it to the pile (existing branch).
        result.AssigneeUserId.Should().BeNull();
        result.AssignmentKind.Should().Be(AssignmentKind.None);
        result.ClaimedAt.Should().BeNull();
        result.Status.Should().Be(ChoreStatus.Active);
        result.LastCompletedAt.Should().Be(NowBase);
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(2);
        AssertTrioInvariant(result);
    }

    [Fact]
    public async Task Complete_RequiredTwo_SameUserTwice_SecondThrows_StillOneRow()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });

        await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, (await ReloadAsync(created.ChoreId)).Version);

        var afterFirst = await ReloadAsync(created.ChoreId);
        var act = async () => await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, afterFirst.Version);

        // The same user contributing twice toward the same occurrence is rejected (D6) — still 1 row.
        await act.Should().ThrowAsync<ChoreValidationException>();
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(1);
    }

    [Fact]
    public async Task Complete_NameOthers_ActorPlusParticipant_SatisfiesInOneCall_TwoRows()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });
        var current = await ReloadAsync(created.ChoreId);

        // Actor Alice names Bob as a co-participant (D7) — [A,B] on a RequiredCount=2 chore satisfies at once.
        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: new[] { Alice, Bob }, current.Version);

        result.LastCompletedAt.Should().Be(NowBase);
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(2);

        await using var ctx = new ApplicationDbContext(_options);
        var contributors = await ctx.ChoreCompletions
            .Where(cc => cc.ChoreId == created.ChoreId)
            .Select(cc => cc.CompletedByUserId)
            .ToListAsync();
        contributors.Should().BeEquivalentTo(new[] { Alice, Bob });
        AssertTrioInvariant(result);
    }

    [Fact]
    public async Task Complete_NameOthers_DuplicateInList_DedupesToOneRow()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });
        var current = await ReloadAsync(created.ChoreId);

        // [A,A] dedupes within the call → a single contributor → 1 row (and a partial, RequiredCount=2 unmet).
        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: new[] { Alice }, current.Version);

        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(1);
        result.LastCompletedAt.Should().BeNull();
        result.LastContributionAt.Should().Be(NowBase);
        AssertTrioInvariant(result);
    }

    [Fact]
    public async Task Complete_NameOthers_NonMember_IsRejected()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });
        var current = await ReloadAsync(created.ChoreId);

        // Carol belongs to another household — naming her must be rejected (D7 member validation, M1).
        var act = async () => await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: new[] { Carol }, current.Version);

        await act.Should().ThrowAsync<ChoreValidationException>();
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(0);
    }

    [Fact]
    public async Task Complete_NameOthers_RepeatActorNamesNewParticipant_DoesNotThrow_AdvancesViaParticipant()
    {
        // D7 escape hatch: a present member (already in priorSet) can record a NEW participant. Even though the
        // actor already contributed, newContributors is non-empty → no D6 throw, and the new participant
        // satisfies the count.
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });

        await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, (await ReloadAsync(created.ChoreId)).Version);

        var afterFirst = await ReloadAsync(created.ChoreId);
        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: new[] { Bob }, afterFirst.Version);

        // Alice (already counted) is silently skipped; Bob is the only new row → 2 distinct → satisfied.
        result.LastCompletedAt.Should().Be(NowBase);
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(2);
        AssertTrioInvariant(result);
    }

    [Fact]
    public async Task Complete_RequiredOne_Regression_AdvancesOnFirstCompletion()
    {
        // The RequiredCount=1 default path is unchanged: one completion satisfies and advances.
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        var current = await ReloadAsync(created.ChoreId);

        var result = await _service.CompleteAsync(
            HouseholdId, created.ChoreId, Alice, null, null, participantUserIds: null, current.Version);

        result.LastCompletedAt.Should().Be(NowBase);
        result.LastContributionAt.Should().Be(NowBase);
        (await CompletionRowCountAsync(created.ChoreId)).Should().Be(1);
        AssertTrioInvariant(result);
    }

    // ---- REQUIRED-COUNT VALIDATION (D2 / ValidateRequiredCount) -------------

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Create_RequiredCountBelowOne_IsRejected(int requiredCount)
    {
        var cmd = FlexibleCmd() with { RequiredCount = requiredCount };
        var act = async () => await _service.CreateChoreAsync(HouseholdId, Alice, cmd);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Create_RequiredCountTwo_IsPersisted()
    {
        var chore = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });
        chore.RequiredCount.Should().Be(2);
        (await ReloadAsync(chore.ChoreId)).RequiredCount.Should().Be(2);
    }

    [Fact]
    public async Task Update_RequiredCountBelowOne_IsRejected()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd() with { RequiredCount = 2 });
        var current = await ReloadAsync(created.ChoreId);
        var cmd = new UpdateChoreCommand(
            Name: "Dishes", Description: null,
            RecurrenceMode: RecurrenceMode.Flexible, IntervalDays: 7, AnchorDate: null,
            DaysOfWeek: null, DayOfMonth: null, EffortTier: EffortTier.Standard,
            OwnerUserId: null, PhotoPath: null, Icon: "", RequiredCount: 0);

        var act = async () => await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, cmd, current.Version);
        await act.Should().ThrowAsync<ChoreValidationException>();
    }

    [Fact]
    public async Task Update_RequiredCount_IsPersisted()
    {
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, FlexibleCmd());
        var current = await ReloadAsync(created.ChoreId);
        var cmd = new UpdateChoreCommand(
            Name: "Dishes", Description: null,
            RecurrenceMode: RecurrenceMode.Flexible, IntervalDays: 7, AnchorDate: null,
            DaysOfWeek: null, DayOfMonth: null, EffortTier: EffortTier.Standard,
            OwnerUserId: null, PhotoPath: null, Icon: "", RequiredCount: 3);

        var updated = await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, cmd, current.Version);
        updated.RequiredCount.Should().Be(3);
        (await ReloadAsync(created.ChoreId)).RequiredCount.Should().Be(3);
    }

    // ---- CONCURRENCY MAPPING (not the xmin detection — that's WP-08) -------

    [Fact]
    public async Task Mutation_WhenSaveRaisesConcurrencyException_SurfacesChoreConflictException()
    {
        // The InMemory provider never raises DbUpdateConcurrencyException, so we cannot trigger a REAL xmin
        // race here (that is WP-08's Postgres job). Instead we verify the MAPPING in isolation: a context
        // whose SaveChangesAsync throws DbUpdateConcurrencyException must surface as ChoreConflictException so
        // the endpoint (WP-06) maps it to 409.
        var dbName = Guid.NewGuid().ToString();
        var throwingOptions = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: dbName)
            .Options;

        // Seed the row through a normal context so the load inside the service succeeds.
        await using (var seed = new ApplicationDbContext(throwingOptions))
        {
            seed.Households.Add(new Household { Id = HouseholdId, Name = "Smith" });
            seed.Users.Add(new User { Id = Alice, HouseholdId = HouseholdId, Email = "a@x.com", DisplayName = "Alice" });
            seed.Chores.Add(new Chore
            {
                HouseholdId = HouseholdId,
                ChoreId = 1,
                Name = "Dishes",
                RecurrenceMode = RecurrenceMode.Flexible,
                IntervalDays = 7,
                EffortTier = EffortTier.Standard,
                EffortPoints = 2,
                Status = ChoreStatus.Active,
                EnteredByUserId = Alice,
                CreatedAt = NowBase.AddDays(-1),
                AssignmentKind = AssignmentKind.None
            });
            await seed.SaveChangesAsync();
        }

        var factoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        factoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ThrowingSaveDbContext(throwingOptions));

        var service = new ChoreService(
            factoryMock.Object,
            new ChoreStatusCalculator(),
            Mock.Of<IImageService>(),
            _clock,
            new Mock<ILogger<ChoreService>>().Object);

        var act = async () => await service.ClaimAsync(HouseholdId, choreId: 1, Alice, version: 0);

        (await act.Should().ThrowAsync<ChoreConflictException>())
            .WithInnerException(typeof(DbUpdateConcurrencyException));
    }

    /// <summary>An InMemory context whose <c>SaveChangesAsync</c> always throws a concurrency exception.</summary>
    private sealed class ThrowingSaveDbContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
            => throw new DbUpdateConcurrencyException("simulated xmin conflict");
    }
}
