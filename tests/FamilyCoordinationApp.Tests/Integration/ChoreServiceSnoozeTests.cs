using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace FamilyCoordinationApp.Tests.Integration;

/// <summary>
/// Service-level verification of the snooze write path against REAL PostgreSQL (V8/V8b/V10/D9). The snooze
/// column + xmin bump + clear-on-complete are exercised through the production <see cref="ChoreService"/> +
/// <see cref="ApplicationDbContext"/>, NOT the InMemory provider (which has no <c>xmin</c> system column and
/// would let a stale-token write silently win). Mirrors <see cref="ChoreServiceConcurrencyTests"/>'s
/// EnsureCreated-on-its-own-database harness exactly.
/// </summary>
[Collection(IntegrationCollection.Name)]
[Trait("kind", "integration")]
public sealed class ChoreServiceSnoozeTests(PostgresContainerFixture postgres) : IAsyncLifetime
{
    private const int HouseholdId = 1;
    private const int Alice = 1;
    private const int Bob = 2;

    private const int FixedWeeklyChoreId = 1;   // Fixed weekly Monday (V8, V10)
    private const int FlexibleChoreId = 2;      // Flexible, pre-snoozed (D9 recurring)
    private const int OneOffChoreId = 3;        // OneOff, pre-snoozed (D9 one-off — guards before-the-fork)

    private static readonly DateTime Now = new(2026, 6, 15, 12, 0, 0, DateTimeKind.Utc); // a Monday
    private static readonly DateOnly Seeded = new(2026, 6, 25);

    private PostgresDbContextFactory _dbFactory = default!;
    private ChoreService _service = default!;

    public async Task InitializeAsync()
    {
        var connectionString = await postgres.CreateDatabaseConnectionStringAsync();
        _dbFactory = new PostgresDbContextFactory(connectionString);

        await using (var ctx = _dbFactory.CreateDbContext())
        {
            await ctx.Database.EnsureCreatedAsync();

            ctx.Households.Add(new Household { Id = HouseholdId, Name = "Snooze House", CreatedAt = Now });
            ctx.Users.AddRange(
                new User { Id = Alice, HouseholdId = HouseholdId, Email = "alice@snooze.test", DisplayName = "Alice", Initials = "A", IsWhitelisted = true, CreatedAt = Now },
                new User { Id = Bob, HouseholdId = HouseholdId, Email = "bob@snooze.test", DisplayName = "Bob", Initials = "B", IsWhitelisted = true, CreatedAt = Now });

            // Fixed weekly Monday — no snooze yet (V8 sets it; V10 snoozes past the slot).
            ctx.Chores.Add(new Chore
            {
                HouseholdId = HouseholdId,
                ChoreId = FixedWeeklyChoreId,
                Name = "Mow (always Monday)",
                RecurrenceMode = RecurrenceMode.Fixed,
                DaysOfWeek = ChoreDaysOfWeek.Monday,
                EffortTier = EffortTier.Standard,
                EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
                Status = ChoreStatus.Active,
                EnteredByUserId = Alice,
                AssignmentKind = AssignmentKind.None,
                CreatedAt = Now
            });

            // Flexible, ALREADY snoozed — a satisfying completion must clear it (D9).
            ctx.Chores.Add(new Chore
            {
                HouseholdId = HouseholdId,
                ChoreId = FlexibleChoreId,
                Name = "Vacuum",
                RecurrenceMode = RecurrenceMode.Flexible,
                IntervalDays = 7,
                SnoozedUntil = Seeded,
                EffortTier = EffortTier.Standard,
                EffortPoints = ChoreEffort.PointsFor(EffortTier.Standard),
                Status = ChoreStatus.Active,
                EnteredByUserId = Alice,
                AssignmentKind = AssignmentKind.None,
                CreatedAt = Now
            });

            // OneOff, ALREADY snoozed — a satisfying completion must clear it too (the before-the-fork guard).
            ctx.Chores.Add(new Chore
            {
                HouseholdId = HouseholdId,
                ChoreId = OneOffChoreId,
                Name = "Fix the gate",
                RecurrenceMode = RecurrenceMode.OneOff,
                AnchorDate = new DateOnly(2026, 6, 20),
                SnoozedUntil = Seeded,
                EffortTier = EffortTier.Quick,
                EffortPoints = ChoreEffort.PointsFor(EffortTier.Quick),
                Status = ChoreStatus.Active,
                EnteredByUserId = Alice,
                AssignmentKind = AssignmentKind.None,
                CreatedAt = Now
            });

            await ctx.SaveChangesAsync();
        }

        _service = new ChoreService(
            _dbFactory,
            new ChoreStatusCalculator(),
            Mock.Of<IImageService>(),
            new FamilyCoordinationApp.Tests.Services.FixedTimeProvider(Now),
            NullLogger<ChoreService>.Instance);
    }

    public Task DisposeAsync() => Task.CompletedTask;

    private async Task<Chore> ReloadAsync(int choreId)
    {
        await using var ctx = _dbFactory.CreateDbContext();
        return await ctx.Chores.AsNoTracking()
            .SingleAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == choreId);
    }

    [Fact]
    public async Task SnoozeAsync_SetsColumn_BumpsVersion_ThenClears_OnRealPostgres()
    {
        // V8. Set the floor: the column persists and the xmin Version advances.
        var before = await ReloadAsync(FixedWeeklyChoreId);
        before.SnoozedUntil.Should().BeNull();

        var snoozed = await _service.SnoozeAsync(HouseholdId, FixedWeeklyChoreId, new DateOnly(2026, 6, 20), before.Version);
        snoozed.SnoozedUntil.Should().Be(new DateOnly(2026, 6, 20));
        snoozed.Version.Should().NotBe(before.Version, "the snooze write advances the xmin token");
        (await ReloadAsync(FixedWeeklyChoreId)).SnoozedUntil.Should().Be(new DateOnly(2026, 6, 20));

        // until: null clears the floor (un-snooze).
        var cleared = await _service.SnoozeAsync(HouseholdId, FixedWeeklyChoreId, null, snoozed.Version);
        cleared.SnoozedUntil.Should().BeNull();
        (await ReloadAsync(FixedWeeklyChoreId)).SnoozedUntil.Should().BeNull();
    }

    [Fact]
    public async Task SnoozeAsync_StaleVersion_Throws_OnRealPostgres()
    {
        // V8. A stale xmin token must surface as ChoreConflictException (→ 409), never last-write-wins.
        var staleVersion = (await ReloadAsync(FixedWeeklyChoreId)).Version;

        var first = await _service.SnoozeAsync(HouseholdId, FixedWeeklyChoreId, new DateOnly(2026, 6, 20), staleVersion);
        first.Version.Should().NotBe(staleVersion);

        var act = async () => await _service.SnoozeAsync(HouseholdId, FixedWeeklyChoreId, new DateOnly(2026, 6, 22), staleVersion);
        await act.Should().ThrowAsync<ChoreConflictException>();
    }

    [Fact]
    public async Task CreateAndUpdate_RoundTripSnoozedUntil_OnRealPostgres()
    {
        // V8b. Catches a dropped ToCommand()/entity wiring: the create path persists SnoozedUntil...
        var created = await _service.CreateChoreAsync(HouseholdId, Alice, new CreateChoreCommand(
            Name: "New flexible chore",
            Description: null,
            RoomId: null,
            RecurrenceMode: RecurrenceMode.Flexible,
            IntervalDays: 7,
            AnchorDate: null,
            DaysOfWeek: null,
            DayOfMonth: null,
            EffortTier: EffortTier.Standard,
            OwnerUserId: null,
            AssigneeUserId: null,
            PhotoPath: null,
            SnoozedUntil: new DateOnly(2026, 6, 25)));

        (await ReloadAsync(created.ChoreId)).SnoozedUntil.Should().Be(new DateOnly(2026, 6, 25));

        // ...and the update path persists a changed SnoozedUntil.
        await _service.UpdateChoreAsync(HouseholdId, created.ChoreId, new UpdateChoreCommand(
            Name: "New flexible chore",
            Description: null,
            RoomId: null,
            RecurrenceMode: RecurrenceMode.Flexible,
            IntervalDays: 7,
            AnchorDate: null,
            DaysOfWeek: null,
            DayOfMonth: null,
            EffortTier: EffortTier.Standard,
            OwnerUserId: null,
            PhotoPath: null,
            SnoozedUntil: new DateOnly(2026, 6, 28)),
            created.Version);

        (await ReloadAsync(created.ChoreId)).SnoozedUntil.Should().Be(new DateOnly(2026, 6, 28));
    }

    [Fact]
    public async Task SnoozingFixedPastSlot_WritesNoCompletion_AndDoesNotAdvanceLastCompleted_OnRealPostgres()
    {
        // V10/M6. Snoozing a Fixed chore past its slot is a pure floor write — it writes ZERO ChoreCompletion
        // rows (equity totals unchanged) and never touches LastCompletedAt.
        var before = await ReloadAsync(FixedWeeklyChoreId);

        await _service.SnoozeAsync(HouseholdId, FixedWeeklyChoreId, new DateOnly(2026, 6, 22), before.Version);

        await using var ctx = _dbFactory.CreateDbContext();
        var completions = await ctx.ChoreCompletions
            .CountAsync(c => c.HouseholdId == HouseholdId && c.ChoreId == FixedWeeklyChoreId);
        completions.Should().Be(0, "a snooze is not a completion");
        (await ReloadAsync(FixedWeeklyChoreId)).LastCompletedAt.Should().BeNull();
    }

    [Fact]
    public async Task SatisfyingCompletion_ClearsSnooze_Recurring_OnRealPostgres()
    {
        // D9. A satisfying completion on a recurring chore clears the snooze floor.
        var chore = await ReloadAsync(FlexibleChoreId);
        chore.SnoozedUntil.Should().Be(Seeded, "seeded snoozed");

        await _service.CompleteAsync(HouseholdId, FlexibleChoreId, Alice, note: null, photoPath: null, participantUserIds: null, chore.Version);

        var after = await ReloadAsync(FlexibleChoreId);
        after.SnoozedUntil.Should().BeNull("a satisfying completion clears the floor (D9)");
        after.LastCompletedAt.Should().Be(Now);
    }

    [Fact]
    public async Task SatisfyingCompletion_ClearsSnooze_OneOff_OnRealPostgres()
    {
        // D9 (the before-the-fork guard). A OneOff satisfying completion clears the floor even though its
        // advance branch differs from the recurring path — proving the clear lands BEFORE the OneOff fork.
        var chore = await ReloadAsync(OneOffChoreId);
        chore.SnoozedUntil.Should().Be(Seeded, "seeded snoozed");

        await _service.CompleteAsync(HouseholdId, OneOffChoreId, Alice, note: null, photoPath: null, participantUserIds: null, chore.Version);

        var after = await ReloadAsync(OneOffChoreId);
        after.SnoozedUntil.Should().BeNull("a OneOff satisfying completion clears the floor too (D9)");
        after.Status.Should().Be(ChoreStatus.Done);
    }
}
