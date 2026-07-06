using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Tests for <see cref="SeedData.SeedChoresAndRoomsAsync"/> (WP-07). The seed must populate a
/// realistic full household — covering every recurrence mode, assignment state, and effort tier,
/// with backdated completion history — and be idempotent + HouseholdId-isolated.
/// </summary>
public class ChoreSeedTests : IDisposable
{
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly Mock<IDbContextFactory<ApplicationDbContext>> _dbFactory;

    public ChoreSeedTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _dbFactory.Setup(f => f.CreateDbContext())
            .Returns(() => new ApplicationDbContext(_options));
        _dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        SeedHouseholdAndUser(householdId: 1, userId: 1, email: "owner@h1.com");
    }

    private void SeedHouseholdAndUser(int householdId, int userId, string email)
    {
        using var context = new ApplicationDbContext(_options);
        if (!context.Households.Any(h => h.Id == householdId))
        {
            context.Households.Add(new Household { Id = householdId, Name = $"Household {householdId}" });
        }

        context.Users.Add(new User { Id = userId, HouseholdId = householdId, Email = email, DisplayName = "Owner" });
        context.SaveChanges();
    }

    public void Dispose() => GC.SuppressFinalize(this);

    [Fact]
    public async Task SeedChoresAndRoomsAsync_PopulatesNamedRoomsAndAtLeastOneGeneralChore()
    {
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        await using var context = new ApplicationDbContext(_options);
        var rooms = await context.Rooms.Where(r => r.HouseholdId == 1).ToListAsync();
        var chores = await context.Chores.Where(c => c.HouseholdId == 1).ToListAsync();

        rooms.Should().NotBeEmpty("each named area gets a Room row");
        rooms.Should().OnlyContain(r => r.HouseholdId == 1);
        rooms.Should().OnlyContain(r => !string.IsNullOrWhiteSpace(r.Name) && !string.IsNullOrWhiteSpace(r.Icon));

        // ≥1 General (roomless) chore — a chore with NO ChoreRoom membership (Phase 13: the shim is gone).
        var memberChoreIds = await context.ChoreRooms
            .Where(cr => cr.HouseholdId == 1)
            .Select(cr => cr.ChoreId)
            .Distinct()
            .ToListAsync();
        chores.Should().Contain(c => !memberChoreIds.Contains(c.ChoreId),
            "at least one chore lives in the virtual General group (no room membership)");
    }

    [Fact]
    public async Task SeedChoresAndRoomsAsync_CoversAllRecurrenceModes_AssignmentStates_AndEffortTiers()
    {
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        await using var context = new ApplicationDbContext(_options);
        var chores = await context.Chores.Where(c => c.HouseholdId == 1).ToListAsync();

        // All three recurrence modes.
        chores.Select(c => c.RecurrenceMode).Distinct()
            .Should().BeEquivalentTo(new[] { RecurrenceMode.OneOff, RecurrenceMode.Fixed, RecurrenceMode.Flexible });

        // All three assignment states.
        chores.Select(c => c.AssignmentKind).Distinct()
            .Should().BeEquivalentTo(new[] { AssignmentKind.None, AssignmentKind.Assigned, AssignmentKind.Claimed });

        // All three effort tiers.
        chores.Select(c => c.EffortTier).Distinct()
            .Should().BeEquivalentTo(new[] { EffortTier.Quick, EffortTier.Standard, EffortTier.BigJob });

        // Fixed weekly-on-weekday (D4-B) is represented.
        chores.Should().Contain(c => c.RecurrenceMode == RecurrenceMode.Fixed
            && c.DaysOfWeek != null && c.DaysOfWeek != ChoreDaysOfWeek.None);
    }

    [Fact]
    public async Task SeedChoresAndRoomsAsync_EffortPointsMatchTier()
    {
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        await using var context = new ApplicationDbContext(_options);
        var chores = await context.Chores.Where(c => c.HouseholdId == 1).ToListAsync();

        chores.Should().OnlyContain(c => c.EffortPoints == ChoreEffort.PointsFor(c.EffortTier));
    }

    [Fact]
    public async Task SeedChoresAndRoomsAsync_SeedsBackdatedCompletionHistory_WithMatchingLastCompletedAt()
    {
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        await using var context = new ApplicationDbContext(_options);
        var completions = await context.ChoreCompletions.Where(cc => cc.HouseholdId == 1).ToListAsync();
        var chores = await context.Chores.Where(c => c.HouseholdId == 1).ToListAsync();

        completions.Should().NotBeEmpty("≥1 chore has backdated completion history so decay renders");

        // Every completion is backdated (in the past) and is mirrored by the chore's LastCompletedAt.
        foreach (var completion in completions)
        {
            completion.CompletedAt.Should().BeBefore(DateTime.UtcNow.AddSeconds(1));
            var chore = chores.Single(c => c.ChoreId == completion.ChoreId);
            chore.LastCompletedAt.Should().NotBeNull();
            chore.LastCompletedAt!.Value.Should().BeCloseTo(completion.CompletedAt, TimeSpan.FromSeconds(1));
        }

        // The failure-criteria converse: every chore carrying a LastCompletedAt has a completion row.
        foreach (var chore in chores.Where(c => c.LastCompletedAt is not null))
        {
            completions.Should().Contain(cc => cc.ChoreId == chore.ChoreId);
        }
    }

    [Fact]
    public async Task SeedChoresAndRoomsAsync_NewChoresHaveActiveStatus_AndInvariantTrioIsConsistent()
    {
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        await using var context = new ApplicationDbContext(_options);
        var chores = await context.Chores.Where(c => c.HouseholdId == 1).ToListAsync();

        chores.Should().OnlyContain(c => c.Status == ChoreStatus.Active);

        // Invariant trio: AssigneeUserId == null ⟺ AssignmentKind == None ⟺ ClaimedAt == null,
        // and a deliberate Assigned chore is never auto-released (ClaimedAt stays null).
        foreach (var chore in chores)
        {
            switch (chore.AssignmentKind)
            {
                case AssignmentKind.None:
                    chore.AssigneeUserId.Should().BeNull();
                    chore.ClaimedAt.Should().BeNull();
                    break;
                case AssignmentKind.Assigned:
                    chore.AssigneeUserId.Should().NotBeNull();
                    chore.ClaimedAt.Should().BeNull();
                    break;
                case AssignmentKind.Claimed:
                    chore.AssigneeUserId.Should().NotBeNull();
                    chore.ClaimedAt.Should().NotBeNull();
                    break;
            }
        }

        // No chore uses the unsupported monthly-on-day recurrence (D4-B ceiling / E5).
        chores.Should().OnlyContain(c => c.DayOfMonth == null);
    }

    [Fact]
    public async Task SeedChoresAndRoomsAsync_IsIdempotent_ReRunDoesNotDuplicate()
    {
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        int roomCount, choreCount, completionCount;
        await using (var context = new ApplicationDbContext(_options))
        {
            roomCount = await context.Rooms.CountAsync(r => r.HouseholdId == 1);
            choreCount = await context.Chores.CountAsync(c => c.HouseholdId == 1);
            completionCount = await context.ChoreCompletions.CountAsync(cc => cc.HouseholdId == 1);
        }

        // Re-run on the populated household — must be a no-op.
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        await using var verify = new ApplicationDbContext(_options);
        (await verify.Rooms.CountAsync(r => r.HouseholdId == 1)).Should().Be(roomCount);
        (await verify.Chores.CountAsync(c => c.HouseholdId == 1)).Should().Be(choreCount);
        (await verify.ChoreCompletions.CountAsync(cc => cc.HouseholdId == 1)).Should().Be(completionCount);
    }

    [Fact]
    public async Task SeedChoresAndRoomsAsync_IsHouseholdIsolated()
    {
        SeedHouseholdAndUser(householdId: 2, userId: 2, email: "owner@h2.com");

        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 1);

        await using var context = new ApplicationDbContext(_options);

        // Household 2 was NOT seeded — its tables stay empty.
        (await context.Rooms.AnyAsync(r => r.HouseholdId == 2)).Should().BeFalse();
        (await context.Chores.AnyAsync(c => c.HouseholdId == 2)).Should().BeFalse();

        // Every seeded row carries household 1, and authorship points at household 1's user.
        (await context.Rooms.Where(r => r.HouseholdId == 1).ToListAsync())
            .Should().OnlyContain(r => r.HouseholdId == 1);
        (await context.Chores.Where(c => c.HouseholdId == 1).ToListAsync())
            .Should().OnlyContain(c => c.HouseholdId == 1 && c.EnteredByUserId == 1);
    }

    [Fact]
    public async Task SeedChoresAndRoomsAsync_NoUser_IsNoOp()
    {
        // A household with no user cannot satisfy the EnteredByUserId FK — seed bails gracefully.
        SeedHouseholdAndUser(householdId: 3, userId: 0, email: "placeholder@h3.com");
        // Remove the user we just added so household 3 has none.
        await using (var prep = new ApplicationDbContext(_options))
        {
            var u = await prep.Users.FirstAsync(x => x.HouseholdId == 3);
            prep.Users.Remove(u);
            await prep.SaveChangesAsync();
        }

        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, householdId: 3);

        await using var context = new ApplicationDbContext(_options);
        (await context.Rooms.AnyAsync(r => r.HouseholdId == 3)).Should().BeFalse();
        (await context.Chores.AnyAsync(c => c.HouseholdId == 3)).Should().BeFalse();
    }
}
