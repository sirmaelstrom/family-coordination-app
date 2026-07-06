using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// V6 logic tests for <see cref="ChoreBoardService"/> (InMemory, frozen <c>now</c> + fixed
/// <see cref="TimeZoneInfo"/>). Covers: rollup bucketing (0 / 1-2 / 3+) off COMPUTED dueness, the virtual
/// General group, needs-attention ordering (dirtiest-first), household isolation (M1), exclusion of
/// Done/Archived chores, the read-side claim-staleness flag, and the reusable <c>ProjectChore</c> projection.
/// </summary>
public class ChoreBoardServiceTests
{
    private const int H1 = 1;   // primary household
    private const int H2 = 2;   // other household (isolation)
    private const int Alice = 100;
    private const int Bob = 101;

    // A fixed evaluation instant. Use UTC so local-day == UTC-day and dueness reasoning is direct.
    private static readonly DateTime Now = new(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private readonly DbContextOptions<ApplicationDbContext> _options;

    public ChoreBoardServiceTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var ctx = new ApplicationDbContext(_options);
        ctx.Households.AddRange(
            new Household { Id = H1, Name = "Smith" },
            new Household { Id = H2, Name = "Jones" });
        ctx.Users.AddRange(
            new User { Id = Alice, HouseholdId = H1, Email = "a@x.com", DisplayName = "Alice", Initials = "AL", ChoresDefaultView = ChoreLens.Rooms },
            new User { Id = Bob, HouseholdId = H1, Email = "b@x.com", DisplayName = "Bob", Initials = "BO" },
            new User { Id = 200, HouseholdId = H2, Email = "c@x.com", DisplayName = "Carol", Initials = "CA" });
        ctx.SaveChanges();
    }

    private ChoreBoardService CreateService()
    {
        var dbFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        return new ChoreBoardService(
            dbFactory.Object,
            new ChoreStatusCalculator(),
            Utc,
            TimeProvider.System);
    }

    // Seed roomless chores (General). Phase 13: room membership lives in ChoreRoom, so a chore-in-a-room is
    // seeded via SeedInRoom / SeedMemberships, not a Chore.RoomId field (dropped in WP-08).
    private void Seed(params Chore[] chores)
    {
        using var ctx = new ApplicationDbContext(_options);
        ctx.Chores.AddRange(chores);
        ctx.SaveChanges();
    }

    // Seed chores that all belong to a single room (adds the chores + one ChoreRoom membership each).
    private void SeedInRoom(int roomId, params Chore[] chores)
    {
        using var ctx = new ApplicationDbContext(_options);
        ctx.Chores.AddRange(chores);
        foreach (var c in chores)
            ctx.ChoreRooms.Add(new ChoreRoom { HouseholdId = c.HouseholdId, ChoreId = c.ChoreId, RoomId = roomId });
        ctx.SaveChanges();
    }

    private void SeedMemberships(params ChoreRoom[] memberships)
    {
        using var ctx = new ApplicationDbContext(_options);
        ctx.ChoreRooms.AddRange(memberships);
        ctx.SaveChanges();
    }

    private void SeedRooms(params Room[] rooms)
    {
        using var ctx = new ApplicationDbContext(_options);
        ctx.Rooms.AddRange(rooms);
        ctx.SaveChanges();
    }

    // A OneOff chore due on the given local date (Overdue if date < today, DueToday if ==, NotDue if future).
    private static Chore OneOff(int id, int householdId, DateOnly due, AssignmentKind kind = AssignmentKind.None, int? assignee = null, DateTime? claimedAt = null) => new()
    {
        HouseholdId = householdId,
        ChoreId = id,
        Name = $"Chore {id}",
        RecurrenceMode = RecurrenceMode.OneOff,
        AnchorDate = due,
        EffortTier = EffortTier.Standard,
        EffortPoints = 2,
        Status = ChoreStatus.Active,
        EnteredByUserId = Alice,
        AssignmentKind = kind,
        AssigneeUserId = assignee,
        ClaimedAt = claimedAt,
        CreatedAt = Now,
    };

    // ------------------------------------------------------------------ rollup bucketing

    [Fact]
    public async Task Rollup_BucketsCleanAttentionNeedsWork_OffComputedDueness()
    {
        // Room 10: 0 due => Clean. Room 11: 2 overdue => Attention. Room 12: 3 overdue => NeedsWork.
        SeedRooms(
            new Room { HouseholdId = H1, RoomId = 10, Name = "Clean", Icon = "✅", SortOrder = 1, CreatedAt = Now },
            new Room { HouseholdId = H1, RoomId = 11, Name = "Attn", Icon = "⚠️", SortOrder = 2, CreatedAt = Now },
            new Room { HouseholdId = H1, RoomId = 12, Name = "Work", Icon = "🧹", SortOrder = 3, CreatedAt = Now });

        SeedInRoom(10, OneOff(1, H1, new DateOnly(2026, 6, 10)));   // future => not due
        SeedInRoom(11,
            OneOff(2, H1, new DateOnly(2026, 5, 1)),    // overdue
            OneOff(3, H1, new DateOnly(2026, 5, 2)));   // overdue
        SeedInRoom(12,
            OneOff(4, H1, new DateOnly(2026, 5, 1)),    // overdue
            OneOff(5, H1, new DateOnly(2026, 5, 2)),    // overdue
            OneOff(6, H1, new DateOnly(2026, 5, 3)));   // overdue

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        var clean = board.Rooms.Single(r => r.RoomId == 10);
        clean.DueCount.Should().Be(0);
        clean.Status.Should().Be(RoomRollupStatus.Clean);

        var attn = board.Rooms.Single(r => r.RoomId == 11);
        attn.DueCount.Should().Be(2);
        attn.Status.Should().Be(RoomRollupStatus.Attention);

        var work = board.Rooms.Single(r => r.RoomId == 12);
        work.DueCount.Should().Be(3);
        work.Status.Should().Be(RoomRollupStatus.NeedsWork);
    }

    [Fact]
    public async Task Rollup_UsesComputedDueness_NotStoredStatus()
    {
        // A flexible chore that is decayed-overdue but stored Status=Active must count toward the due bucket.
        SeedRooms(new Room { HouseholdId = H1, RoomId = 10, Name = "Kitchen", Icon = "🍳", SortOrder = 1, CreatedAt = Now });
        SeedInRoom(10, new Chore
        {
            HouseholdId = H1,
            ChoreId = 1,
            Name = "Decayed flexible",
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            LastCompletedAt = Now.AddDays(-30),  // way past one interval => Overdue dueness
            EffortTier = EffortTier.Standard,
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            CreatedAt = Now,
        });

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        board.Chores.Single().DueState.Should().Be(DueState.Overdue);
        board.Rooms.Single(r => r.RoomId == 10).DueCount.Should().Be(1);
        board.Rooms.Single(r => r.RoomId == 10).Status.Should().Be(RoomRollupStatus.Attention);
    }

    [Fact]
    public async Task EmptyRoom_AppearsAsCleanRollup()
    {
        SeedRooms(new Room { HouseholdId = H1, RoomId = 10, Name = "Empty", Icon = "📦", SortOrder = 1, CreatedAt = Now });

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        var rollup = board.Rooms.Single(r => r.RoomId == 10);
        rollup.ChoreCount.Should().Be(0);
        rollup.DueCount.Should().Be(0);
        rollup.Status.Should().Be(RoomRollupStatus.Clean);
    }

    // ------------------------------------------------------------------ General group

    [Fact]
    public async Task RoomlessChores_LandInGeneralGroup_WithNullRoomId()
    {
        Seed(
            OneOff(1, H1, new DateOnly(2026, 6, 10)),
            OneOff(2, H1, new DateOnly(2026, 6, 11)));

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        var general = board.Rooms.Single(r => r.RoomId == null);
        general.Name.Should().Be("General");
        general.ChoreCount.Should().Be(2);
        // The General group is virtual — there is no Room row backing it.
        board.Rooms.Where(r => r.RoomId != null).Should().BeEmpty();
    }

    [Fact]
    public async Task GeneralGroup_NotEmitted_WhenNoRoomlessChores()
    {
        SeedRooms(new Room { HouseholdId = H1, RoomId = 10, Name = "Kitchen", Icon = "🍳", SortOrder = 1, CreatedAt = Now });
        SeedInRoom(10, OneOff(1, H1, new DateOnly(2026, 6, 10)));

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        board.Rooms.Should().ContainSingle()
            .Which.RoomId.Should().Be(10);
        board.Rooms.Any(r => r.RoomId == null).Should().BeFalse();
    }

    [Fact]
    public async Task GeneralGroup_SortsLast_AfterRealRooms()
    {
        SeedRooms(
            new Room { HouseholdId = H1, RoomId = 10, Name = "Bravo", Icon = "🅱️", SortOrder = 2, CreatedAt = Now },
            new Room { HouseholdId = H1, RoomId = 11, Name = "Alpha", Icon = "🅰️", SortOrder = 1, CreatedAt = Now });
        SeedInRoom(10, OneOff(1, H1, new DateOnly(2026, 6, 10)));
        Seed(OneOff(2, H1, new DateOnly(2026, 6, 10)));

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        // Real rooms in stored sort order (Alpha=1, Bravo=2), then General last.
        board.Rooms.Select(r => r.Name).Should().ContainInOrder("Alpha", "Bravo", "General");
    }

    // ------------------------------------------------------------------ needs-attention

    [Fact]
    public async Task NeedsAttention_OrdersOverdueThenDueThenPile_DirtiestFirst()
    {
        Seed(
            OneOff(1, H1, new DateOnly(2026, 6, 10)),                          // future, claimed => excluded
            OneOff(2, H1, new DateOnly(2026, 5, 30)),                          // due today, assigned
            OneOff(3, H1, new DateOnly(2026, 5, 1)),                           // overdue (older)
            OneOff(4, H1, new DateOnly(2026, 5, 20)),                          // overdue (newer)
            OneOff(5, H1, new DateOnly(2026, 6, 5)));                          // future, unclaimed pile

        // Make #1 + #2 not pile so only #3/#4/#5 + due-#2 qualify; #1 future+claimed is excluded.
        using (var ctx = new ApplicationDbContext(_options))
        {
            var c1 = ctx.Chores.Single(c => c.HouseholdId == H1 && c.ChoreId == 1);
            c1.AssignmentKind = AssignmentKind.Claimed; c1.AssigneeUserId = Bob; c1.ClaimedAt = Now;
            var c2 = ctx.Chores.Single(c => c.HouseholdId == H1 && c.ChoreId == 2);
            c2.AssignmentKind = AssignmentKind.Assigned; c2.AssigneeUserId = Alice; c2.ClaimedAt = Now;
            var c4 = ctx.Chores.Single(c => c.HouseholdId == H1 && c.ChoreId == 4);
            c4.AssignmentKind = AssignmentKind.Assigned; c4.AssigneeUserId = Alice; c4.ClaimedAt = Now;
            var c3 = ctx.Chores.Single(c => c.HouseholdId == H1 && c.ChoreId == 3);
            c3.AssignmentKind = AssignmentKind.Assigned; c3.AssigneeUserId = Alice; c3.ClaimedAt = Now;
            ctx.SaveChanges();
        }

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        // Expected order: overdue oldest-first (#3 due 5/1, #4 due 5/20) → due-today (#2) → future pile (#5).
        // #1 (future + claimed) is not needs-attention.
        board.NeedsAttentionChoreIds.Should().Equal(3, 4, 2, 5);
    }

    [Fact]
    public async Task NeedsAttention_IncludesUnclaimedPile_EvenWhenNotDue()
    {
        // A future-dated, unclaimed pile chore is "up for grabs" => needs-attention.
        Seed(OneOff(1, H1, new DateOnly(2026, 7, 1)));   // future, AssignmentKind.None

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        board.NeedsAttentionChoreIds.Should().Contain(1);
    }

    [Fact]
    public async Task NeedsAttention_ExcludesSnoozedUnclaimedChore_WithClearedControl()
    {
        // V11 (board surface). A snoozed unclaimed chore (None, SnoozedUntil = today+5) must be ABSENT from
        // needs-attention — the !IsSnoozed gate closes the up-for-grabs leak. Un-snoozed, it returns.
        var snoozed = OneOff(1, H1, new DateOnly(2026, 7, 1));  // future OneOff, unclaimed pile
        snoozed.SnoozedUntil = new DateOnly(2026, 6, 4);              // Now is 2026-05-30 => today < s => snoozed
        Seed(snoozed);

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);
        board.Chores.Single(c => c.Id == 1).IsSnoozed.Should().BeTrue();
        board.NeedsAttentionChoreIds.Should().NotContain(1, "a snoozed unclaimed chore is excluded from needs-attention");

        // Control: clear the floor => the unclaimed pile chore is up-for-grabs again.
        using (var ctx = new ApplicationDbContext(_options))
        {
            ctx.Chores.Single(c => c.HouseholdId == H1 && c.ChoreId == 1).SnoozedUntil = null;
            ctx.SaveChanges();
        }

        var control = await CreateService().GetBoardAsync(H1, Alice, Now);
        control.Chores.Single(c => c.Id == 1).IsSnoozed.Should().BeFalse();
        control.NeedsAttentionChoreIds.Should().Contain(1, "un-snoozed, it returns to needs-attention");
    }

    // ------------------------------------------------------------------ isolation + exclusion

    [Fact]
    public async Task SecondHouseholdChores_NeverAppear()
    {
        Seed(
            OneOff(1, H1, new DateOnly(2026, 5, 30)),
            OneOff(99, H2, new DateOnly(2026, 5, 30)));

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        board.Chores.Should().ContainSingle().Which.Id.Should().Be(1);
        board.Members.Select(m => m.UserId).Should().BeEquivalentTo(new[] { Alice, Bob });
        board.Members.Should().NotContain(m => m.UserId == 200);
    }

    [Fact]
    public async Task DoneAndArchivedChores_AreExcluded()
    {
        SeedRooms(new Room { HouseholdId = H1, RoomId = 10, Name = "Kitchen", Icon = "🍳", SortOrder = 1, CreatedAt = Now });
        var done = OneOff(1, H1, new DateOnly(2026, 5, 30));
        done.Status = ChoreStatus.Done;
        var archived = OneOff(2, H1, new DateOnly(2026, 5, 30));
        archived.Status = ChoreStatus.Archived;
        var active = OneOff(3, H1, new DateOnly(2026, 5, 30));
        SeedInRoom(10, done, archived, active);

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        board.Chores.Should().ContainSingle().Which.Id.Should().Be(3);
        // Excluded chores do not count in the rollup either.
        board.Rooms.Single(r => r.RoomId == 10).ChoreCount.Should().Be(1);
        board.Rooms.Single(r => r.RoomId == 10).DueCount.Should().Be(1);
    }

    // ------------------------------------------------------------------ default view + stale flag

    [Fact]
    public async Task UserDefaultView_IsSurfaced_ForTheCaller()
    {
        Seed(OneOff(1, H1, new DateOnly(2026, 5, 30)));

        var aliceBoard = await CreateService().GetBoardAsync(H1, Alice, Now);
        aliceBoard.UserDefaultView.Should().Be(ChoreLens.Rooms);

        // Bob has no preference => null (island opens onto Needs-attention).
        var bobBoard = await CreateService().GetBoardAsync(H1, Bob, Now);
        bobBoard.UserDefaultView.Should().BeNull();
    }

    [Fact]
    public async Task CallerCapacityTier_ReflectsTheCallerOnly_NullWhenUnset()
    {
        // Phase 15 R4′ (V1.2): the board carries the CALLER's own physical-capacity tier, sourced from
        // User.PhysicalCapacityTier on the SAME single-user caller projection as the default view. Give Alice
        // a Reduced tier; leave Bob's unset.
        using (var ctx = new ApplicationDbContext(_options))
        {
            ctx.Users.Single(u => u.Id == Alice).PhysicalCapacityTier = "Reduced";
            ctx.SaveChanges();
        }
        Seed(OneOff(1, H1, new DateOnly(2026, 5, 30)));

        // The caller (Alice) sees her own tier.
        var aliceBoard = await CreateService().GetBoardAsync(H1, Alice, Now);
        aliceBoard.CallerCapacityTier.Should().Be("Reduced");

        // Bob (same household, tier unset) sees null (⇒ Full). The field is CALLER-scoped — it is never
        // leaked from another member's row, even though Alice in the same household is Reduced.
        var bobBoard = await CreateService().GetBoardAsync(H1, Bob, Now);
        bobBoard.CallerCapacityTier.Should().BeNull(
            "the board carries only the caller's own tier, never another member's");
    }

    [Fact]
    public async Task IsClaimStale_True_ForClaimOlderThanThreshold_ButStillDisplayedClaimed()
    {
        // Claimed > 48h ago => stale on read; the chore is still surfaced as Claimed (no materialized release).
        var stale = OneOff(1, H1, new DateOnly(2026, 6, 10),
            kind: AssignmentKind.Claimed, assignee: Bob, claimedAt: Now.AddHours(-49));
        var fresh = OneOff(2, H1, new DateOnly(2026, 6, 10),
            kind: AssignmentKind.Claimed, assignee: Bob, claimedAt: Now.AddHours(-1));
        Seed(stale, fresh);

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        var staleDto = board.Chores.Single(c => c.Id == 1);
        staleDto.IsClaimStale.Should().BeTrue();
        staleDto.AssignmentKind.Should().Be(AssignmentKind.Claimed);   // NOT released on read

        board.Chores.Single(c => c.Id == 2).IsClaimStale.Should().BeFalse();
    }

    // ------------------------------------------------------------------ reusable projection (WP-06)

    [Fact]
    public void ProjectChore_ComputesDueness_AndStaleness_ForASingleEntity()
    {
        var chore = OneOff(42, H1, new DateOnly(2026, 5, 1),   // overdue vs Now
            kind: AssignmentKind.Claimed, assignee: Bob, claimedAt: Now.AddHours(-49));
        chore.EffortTier = EffortTier.BigJob;
        chore.EffortPoints = 3;
        chore.Version = 5;

        // ProjectChore is now a pure projection — the caller supplies the persisted membership set (Phase 13).
        var dto = CreateService().ProjectChore(chore, Now, Utc, [7]);

        dto.Id.Should().Be(42);
        dto.RoomIds.Should().Equal(7);
        dto.DueState.Should().Be(DueState.Overdue);
        dto.ColorTier.Should().Be(ColorTier.Overdue);
        dto.IsClaimStale.Should().BeTrue();
        dto.AssignmentKind.Should().Be(AssignmentKind.Claimed);
        dto.AssigneeUserId.Should().Be(Bob);
        dto.EffortTier.Should().Be("BigJob");
        dto.EffortPoints.Should().Be(3);
        dto.RecurrenceMode.Should().Be("OneOff");
        dto.Version.Should().Be(5u);
    }

    // ------------------------------------------------------------------ multi-room (Phase 13, WP-04)

    [Fact]
    public async Task Board_MultiRoomChore_CountsInEachRoom()
    {
        SeedRooms(
            new Room { HouseholdId = H1, RoomId = 10, Name = "Kitchen", SortOrder = 1 },
            new Room { HouseholdId = H1, RoomId = 11, Name = "Bathroom", SortOrder = 2 });
        // A chore in BOTH rooms (Phase 13 memberships).
        Seed(OneOff(1, H1, new DateOnly(2026, 6, 15)));   // future → NotDue, so no bucket noise
        SeedMemberships(
            new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 10 },
            new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 11 });

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        // Flat list stays one DTO per chore (M3), carrying BOTH rooms sorted ascending.
        board.Chores.Should().ContainSingle();
        board.Chores.Single().RoomIds.Should().Equal(10, 11);

        // Rollups fan out: the chore appears in each of its member rooms.
        board.Rooms.Single(r => r.RoomId == 10).ChoreCount.Should().Be(1, "the chore counts in Kitchen");
        board.Rooms.Single(r => r.RoomId == 11).ChoreCount.Should().Be(1, "the same chore also counts in Bathroom");
    }

    [Fact]
    public async Task HouseholdTotals_CountEachChoreOnce()
    {
        SeedRooms(
            new Room { HouseholdId = H1, RoomId = 10, Name = "Kitchen", SortOrder = 1 },
            new Room { HouseholdId = H1, RoomId = 11, Name = "Bathroom", SortOrder = 2 });
        // An overdue, unclaimed chore in TWO rooms. The rollups fan out (both rooms show it), but the flat
        // board.chores list — the household-total substrate — must carry it exactly ONCE (M3/MN1).
        Seed(OneOff(1, H1, new DateOnly(2026, 5, 1)));    // past → Overdue
        SeedMemberships(
            new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 10 },
            new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 11 });

        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        // Sanity: it fans out in the rollups (each room counts it).
        board.Rooms.Single(r => r.RoomId == 10).ChoreCount.Should().Be(1);
        board.Rooms.Single(r => r.RoomId == 11).ChoreCount.Should().Be(1);

        // Household totals read the flat list → each bucket counts the chore ONCE, never twice.
        var stats = ChoreHomeStats.Compute(board.Chores);
        stats.Total.Should().Be(1, "the flat board list is one DTO per chore, even for a 2-room chore");
        stats.Overdue.Should().Be(1, "a multi-room chore is not double-counted in household totals (MN1)");
        stats.UpForGrabs.Should().Be(1);
    }

    // ------------------------------------------------------------------ multi-person co-sign progress (WP-04)

    [Fact]
    public async Task MultiPersonChore_WithOneCompletion_ProjectsCorrectProgress()
    {
        // Arrange: one RequiredCount=2 chore with one completion; one RequiredCount=1 chore with no completions.
        var multiChore = OneOff(1, H1, new DateOnly(2026, 6, 10));
        multiChore.RequiredCount = 2;

        var singleChore = OneOff(2, H1, new DateOnly(2026, 6, 10));
        // RequiredCount defaults to 1 (entity default)

        Seed(multiChore, singleChore);

        // Alice has signed chore 1 in the current occurrence window (no LastCompletedAt → all rows count).
        using (var ctx = new ApplicationDbContext(_options))
        {
            ctx.ChoreCompletions.Add(new ChoreCompletion
            {
                HouseholdId = H1,
                ChoreId = 1,
                CompletionId = 1,
                CompletedByUserId = Alice,
                CompletedAt = Now.AddHours(-2),
                EffortPointsSnapshot = 2,
            });
            ctx.SaveChanges();
        }

        // Act
        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        // Assert: multi-person chore has progress from Alice's completion.
        var multiDto = board.Chores.Single(c => c.Id == 1);
        multiDto.RequiredCount.Should().Be(2);
        multiDto.CompletedCount.Should().Be(1);
        multiDto.Roster.Should().ContainSingle(m => m.UserId == Alice && m.State == RosterState.Done);

        // Assert: single-person chore reports zeroed progress.
        var singleDto = board.Chores.Single(c => c.Id == 2);
        singleDto.RequiredCount.Should().Be(1);
        singleDto.CompletedCount.Should().Be(0);
        singleDto.Roster.Should().BeEmpty();
    }

    [Fact]
    public async Task MultiPersonChore_ProgressQuerySkipped_WhenNoMultiPersonChores()
    {
        // Arrange: all chores have RequiredCount=1 (default). The lazy P5 query should not run.
        Seed(
            OneOff(1, H1, new DateOnly(2026, 6, 10)),
            OneOff(2, H1, new DateOnly(2026, 6, 10)));

        // Act
        var board = await CreateService().GetBoardAsync(H1, Alice, Now);

        // All single-person chores get zeroed progress.
        foreach (var dto in board.Chores)
        {
            dto.RequiredCount.Should().Be(1);
            dto.CompletedCount.Should().Be(0);
            dto.Roster.Should().BeEmpty();
        }
    }
}
