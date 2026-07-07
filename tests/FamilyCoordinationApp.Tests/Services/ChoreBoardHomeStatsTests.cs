using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Dtos;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Semantics lock for the lean <see cref="ChoreBoardService.GetHomeStatsAsync"/> count read (the dashboard's
/// chore summary). The keystone is the EQUIVALENCE test: on a representative seed (overdue / due-today /
/// future / snoozed / stale-claim / fresh-claim / multi-room / Done / Archived / other-household), the lean
/// method must return counts EQUAL to deriving them from <see cref="ChoreBoardService.GetBoardAsync"/>'s
/// flat <c>Chores</c> list via the <see cref="ChoreHomeStats"/> reducer — the exact composition
/// <c>DashboardService</c> used before the swap. Concrete literals are asserted too, so both paths can't
/// silently drift together. (InMemory, frozen <c>now</c> + fixed UTC <see cref="TimeZoneInfo"/> — mirrors
/// <see cref="ChoreBoardServiceTests"/>.)
/// </summary>
public class ChoreBoardHomeStatsTests
{
    private const int H1 = 1;   // primary household
    private const int H2 = 2;   // other household (isolation)
    private const int Alice = 100;
    private const int Bob = 101;

    // A fixed evaluation instant. Use UTC so local-day == UTC-day and dueness reasoning is direct.
    private static readonly DateTime Now = new(2026, 5, 30, 12, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = new(2026, 5, 30);
    private static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;

    private readonly DbContextOptions<ApplicationDbContext> _options;

    public ChoreBoardHomeStatsTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        using var ctx = new ApplicationDbContext(_options);
        ctx.Households.AddRange(
            new Household { Id = H1, Name = "Smith" },
            new Household { Id = H2, Name = "Jones" });
        ctx.Users.AddRange(
            new User { Id = Alice, HouseholdId = H1, Email = "a@x.com", DisplayName = "Alice", Initials = "AL" },
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

    private void Seed(params Chore[] chores)
    {
        using var ctx = new ApplicationDbContext(_options);
        ctx.Chores.AddRange(chores);
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
    private static Chore OneOff(
        int id,
        int householdId,
        DateOnly due,
        AssignmentKind kind = AssignmentKind.None,
        int? assignee = null,
        DateTime? claimedAt = null,
        DateOnly? snoozedUntil = null,
        ChoreStatus status = ChoreStatus.Active) => new()
    {
        HouseholdId = householdId,
        ChoreId = id,
        Name = $"Chore {id}",
        RecurrenceMode = RecurrenceMode.OneOff,
        AnchorDate = due,
        EffortTier = EffortTier.Standard,
        EffortPoints = 2,
        Status = status,
        EnteredByUserId = Alice,
        AssignmentKind = kind,
        AssigneeUserId = assignee,
        ClaimedAt = claimedAt,
        SnoozedUntil = snoozedUntil,
        CreatedAt = Now,
    };

    /// <summary>
    /// The representative seed the equivalence test runs on — one chore per counting concern (see the
    /// inline notes), plus the exclusions (Done / Archived / other household).
    /// </summary>
    private void SeedRepresentativeScenario()
    {
        SeedRooms(
            new Room { HouseholdId = H1, RoomId = 10, Name = "Kitchen", Icon = "🍳", SortOrder = 1, CreatedAt = Now },
            new Room { HouseholdId = H1, RoomId = 11, Name = "Bathroom", Icon = "🛁", SortOrder = 2, CreatedAt = Now });

        Seed(
            // 1: overdue + unassigned            → Overdue, UpForGrabs
            OneOff(1, H1, new DateOnly(2026, 5, 1)),
            // 2: due today, deliberately assigned → DueToday only (Assigned ≠ up-for-grabs)
            OneOff(2, H1, Today, AssignmentKind.Assigned, Bob),
            // 3: future + unassigned              → UpForGrabs only (dueness-independent pile membership)
            OneOff(3, H1, new DateOnly(2026, 6, 15)),
            // 4: would-be-overdue but SNOOZED past today, unassigned → Total only. The snooze gate reads
            //    Scheduled (drops it from Overdue) AND the !IsSnoozed guard drops it from UpForGrabs.
            OneOff(4, H1, new DateOnly(2026, 5, 1), snoozedUntil: new DateOnly(2026, 6, 5)),
            // 6: future, FRESH claim (1h old < 48h threshold) → not up-for-grabs
            OneOff(6, H1, new DateOnly(2026, 6, 20), AssignmentKind.Claimed, Bob, claimedAt: Now.AddHours(-1)),
            // 7: overdue + unassigned, member of BOTH rooms → must count ONCE in every bucket (MN1)
            OneOff(7, H1, new DateOnly(2026, 5, 2)),
            // 8: stored Done  → excluded from the active set entirely
            OneOff(8, H1, new DateOnly(2026, 5, 1), status: ChoreStatus.Done),
            // 9: stored Archived → excluded from the active set entirely
            OneOff(9, H1, new DateOnly(2026, 5, 1), status: ChoreStatus.Archived),
            // 20: other household's overdue unassigned chore → NEVER visible to H1 (M1)
            OneOff(20, H2, new DateOnly(2026, 5, 1)));

        // 5: recurring flexible, decayed way past its interval (Overdue) with a STALE claim (3 days > 48h)
        //    → Overdue AND UpForGrabs (stale-claim arm of the pile predicate).
        Seed(new Chore
        {
            HouseholdId = H1,
            ChoreId = 5,
            Name = "Decayed stale-claimed flexible",
            RecurrenceMode = RecurrenceMode.Flexible,
            IntervalDays = 7,
            LastCompletedAt = Now.AddDays(-30),
            EffortTier = EffortTier.Standard,
            EffortPoints = 2,
            Status = ChoreStatus.Active,
            EnteredByUserId = Alice,
            AssignmentKind = AssignmentKind.Claimed,
            AssigneeUserId = Bob,
            ClaimedAt = Now.AddDays(-3),
            CreatedAt = Now,
        });

        SeedMemberships(
            new ChoreRoom { HouseholdId = H1, ChoreId = 7, RoomId = 10 },
            new ChoreRoom { HouseholdId = H1, ChoreId = 7, RoomId = 11 });
    }

    // ------------------------------------------------------------------ the equivalence lock

    [Fact]
    public async Task HomeStats_EqualBoardDerivedCounts_OnRepresentativeScenario()
    {
        SeedRepresentativeScenario();
        var service = CreateService();

        // The OLD dashboard composition: full board build → ChoreHomeStats reducer over the flat list.
        var board = await service.GetBoardAsync(H1, Alice, Now);
        var derived = ChoreHomeStats.Compute(board.Chores);

        // The NEW lean read.
        var stats = await service.GetHomeStatsAsync(H1, Now);

        // Field-for-field equivalence — the point of the exercise.
        stats.ActiveTotal.Should().Be(derived.Total);
        stats.Overdue.Should().Be(derived.Overdue);
        stats.DueToday.Should().Be(derived.DueToday);
        stats.UpForGrabs.Should().Be(derived.UpForGrabs);

        // Concrete literals too, so the two paths can't drift together unnoticed:
        // active = {1,2,3,4,5,6,7} (Done 8 / Archived 9 / H2's 20 excluded); multi-room 7 counts ONCE.
        stats.ActiveTotal.Should().Be(7, "snoozed chores stay in the active total; Done/Archived/H2 are out");
        stats.Overdue.Should().Be(3, "chores 1, 5, 7 — the snoozed 4 reads Scheduled, not Overdue");
        stats.DueToday.Should().Be(1, "chore 2 is due on the frozen today");
        stats.UpForGrabs.Should().Be(4, "unassigned 1, 3, 7 + stale-claimed 5; snoozed 4 and fresh-claimed 6 are out");
    }

    // ------------------------------------------------------------------ per-concern guards

    [Fact]
    public async Task HomeStats_MultiRoomChore_CountsOnce()
    {
        SeedRooms(
            new Room { HouseholdId = H1, RoomId = 10, Name = "Kitchen", SortOrder = 1, CreatedAt = Now },
            new Room { HouseholdId = H1, RoomId = 11, Name = "Bathroom", SortOrder = 2, CreatedAt = Now });
        Seed(OneOff(1, H1, new DateOnly(2026, 5, 1)));   // overdue + unassigned
        SeedMemberships(
            new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 10 },
            new ChoreRoom { HouseholdId = H1, ChoreId = 1, RoomId = 11 });

        var stats = await CreateService().GetHomeStatsAsync(H1, Now);

        stats.Should().Be(new ChoreHomeStatsDto(
            ActiveTotal: 1, Overdue: 1, DueToday: 0, UpForGrabs: 1),
            "a 2-room chore is one chore — room fan-out is a rollup concern, never a count multiplier (MN1)");
    }

    [Fact]
    public async Task HomeStats_SnoozedChore_ExcludedFromOverdueAndUpForGrabs_ButInTotal()
    {
        // Would be Overdue + UpForGrabs if not snoozed.
        Seed(OneOff(1, H1, new DateOnly(2026, 5, 1), snoozedUntil: new DateOnly(2026, 6, 5)));

        var stats = await CreateService().GetHomeStatsAsync(H1, Now);

        stats.Should().Be(new ChoreHomeStatsDto(ActiveTotal: 1, Overdue: 0, DueToday: 0, UpForGrabs: 0));
    }

    [Fact]
    public async Task HomeStats_FiltersByHousehold()
    {
        SeedRepresentativeScenario();

        var stats = await CreateService().GetHomeStatsAsync(H2, Now);

        stats.Should().Be(new ChoreHomeStatsDto(ActiveTotal: 1, Overdue: 1, DueToday: 0, UpForGrabs: 1),
            "H2 sees only its own chore 20 — never H1's board (M1)");
    }

    [Fact]
    public async Task HomeStats_EmptyHousehold_ReturnsZeros()
    {
        var stats = await CreateService().GetHomeStatsAsync(H1, Now);

        stats.Should().Be(new ChoreHomeStatsDto(0, 0, 0, 0));
    }
}
