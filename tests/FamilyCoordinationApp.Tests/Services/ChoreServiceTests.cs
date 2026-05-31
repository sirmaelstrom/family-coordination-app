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
        _seedContext.SaveChanges();
    }

    public void Dispose()
    {
        _seedContext.Dispose();
        GC.SuppressFinalize(this);
    }

    private static CreateChoreCommand FlexibleCmd(int? assignee = null) => new(
        Name: "Dishes",
        Description: "  rinse first  ",
        RoomId: null,
        RecurrenceMode: RecurrenceMode.Flexible,
        IntervalDays: 7,
        AnchorDate: null,
        DaysOfWeek: null,
        DayOfMonth: null,
        EffortTier: EffortTier.Standard,
        OwnerUserId: null,
        AssigneeUserId: assignee,
        PhotoPath: null);

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
            RoomId: null,
            RecurrenceMode: RecurrenceMode.Flexible,
            IntervalDays: 3,
            AnchorDate: null,
            DaysOfWeek: null,
            DayOfMonth: null,
            EffortTier: EffortTier.BigJob,
            OwnerUserId: Alice,
            PhotoPath: null);

        var updated = await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, updateCmd, current.Version);

        updated.Name.Should().Be("Wash dishes");
        updated.IntervalDays.Should().Be(3);
        updated.EffortTier.Should().Be(EffortTier.BigJob);
        updated.EffortPoints.Should().Be(ChoreEffort.PointsFor(EffortTier.BigJob));
        updated.OwnerUserId.Should().Be(Alice);

        // Assignment triple untouched by an edit (Bob still holds the claim).
        updated.AssigneeUserId.Should().Be(Bob);
        updated.AssignmentKind.Should().Be(AssignmentKind.Claimed);
    }

    [Fact]
    public async Task Update_MissingChore_ThrowsNotFound()
    {
        var cmd = new UpdateChoreCommand("X", null, null, RecurrenceMode.OneOff, null, null, null, null, EffortTier.Quick, null, null);
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

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Bob, note: " good ", photoPath: null, current.Version);

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
        await _service.CompleteAsync(HouseholdId, created.ChoreId, Alice, note: null, photoPath: null, current.Version);

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
        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Bob, null, null, current.Version);

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

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Bob, null, null, current.Version);

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

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Alice, null, null, current.Version);

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

        var result = await _service.CompleteAsync(HouseholdId, created.ChoreId, Alice, null, null, current.Version);

        // Fixed completion mutates NO recurrence field beyond LastCompletedAt — the anchor is untouched.
        result.AnchorDate.Should().Be(anchor);
        result.IntervalDays.Should().Be(7);
        result.LastCompletedAt.Should().Be(NowBase);
    }

    [Fact]
    public async Task Complete_MissingChore_ThrowsNotFound()
    {
        var act = async () => await _service.CompleteAsync(HouseholdId, choreId: 99, Alice, null, null, version: 0);
        await act.Should().ThrowAsync<ChoreNotFoundException>();
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
