using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// Tests for <see cref="SeedData.SeedDevEquityDataAsync"/> (WP-07 equity enrichment).
/// Verifies multi-member creation, cross-member completion spread, idempotency, and that
/// the prod path (<c>SetupService.CreateHouseholdAsync</c>) is unaffected (MN11).
/// </summary>
public class ChoreSeedEquityTests : IDisposable
{
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly Mock<IDbContextFactory<ApplicationDbContext>> _dbFactory;
    private const int HouseholdId = 1;
    private const int SeedUserId = 1;

    public ChoreSeedEquityTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _dbFactory = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _dbFactory.Setup(f => f.CreateDbContext())
            .Returns(() => new ApplicationDbContext(_options));
        _dbFactory.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        // Seed a household with a single "Justin" user and the chore library so equity seed has
        // chores to attach completions to.
        SeedHouseholdAndUser(HouseholdId, SeedUserId, "justin@dev.local", "Justin");
    }

    private void SeedHouseholdAndUser(int householdId, int userId, string email, string displayName)
    {
        using var context = new ApplicationDbContext(_options);
        if (!context.Households.Any(h => h.Id == householdId))
        {
            context.Households.Add(new Household { Id = householdId, Name = $"Household {householdId}" });
            context.SaveChanges();
        }

        if (!context.Users.Any(u => u.Id == userId && u.HouseholdId == householdId))
        {
            context.Users.Add(new User
            {
                Id = userId,
                HouseholdId = householdId,
                Email = email,
                DisplayName = displayName,
                Initials = displayName[..1],
                IsWhitelisted = true,
                CreatedAt = DateTime.UtcNow
            });
            context.SaveChanges();
        }
    }

    /// Seed the chore library first so equity seed has chore rows to attach completions to.
    private async Task SeedChoresAsync()
        => await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, HouseholdId);

    public void Dispose() => GC.SuppressFinalize(this);

    // -------------------------------------------------------------------------
    // Core contract
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SeedDevEquityDataAsync_CreatesAtLeastThreeMembers()
    {
        await SeedChoresAsync();

        await SeedData.SeedDevEquityDataAsync(_dbFactory.Object, HouseholdId);

        await using var context = new ApplicationDbContext(_options);
        var memberCount = await context.Users.CountAsync(u => u.HouseholdId == HouseholdId);
        memberCount.Should().BeGreaterThanOrEqualTo(3, "the equity seed adds Natalie, Tristan, and Samantha");
    }

    [Fact]
    public async Task SeedDevEquityDataAsync_CompletionsSpanMoreThanOneAuthor()
    {
        await SeedChoresAsync();

        await SeedData.SeedDevEquityDataAsync(_dbFactory.Object, HouseholdId);

        await using var context = new ApplicationDbContext(_options);
        var authorCount = await context.ChoreCompletions
            .Where(cc => cc.HouseholdId == HouseholdId)
            .Select(cc => cc.CompletedByUserId)
            .Distinct()
            .CountAsync();

        authorCount.Should().BeGreaterThan(1, "completions must be distributed across multiple members");
    }

    [Fact]
    public async Task SeedDevEquityDataAsync_PerMemberPointsAreNonZero()
    {
        await SeedChoresAsync();

        await SeedData.SeedDevEquityDataAsync(_dbFactory.Object, HouseholdId);

        await using var context = new ApplicationDbContext(_options);
        var byAuthor = await context.ChoreCompletions
            .Where(cc => cc.HouseholdId == HouseholdId)
            .GroupBy(cc => cc.CompletedByUserId)
            .Select(g => new { UserId = g.Key, Points = g.Sum(cc => cc.EffortPointsSnapshot) })
            .ToListAsync();

        byAuthor.Should().OnlyContain(g => g.Points > 0, "every author in the seed must have at least one point");
    }

    [Fact]
    public async Task SeedDevEquityDataAsync_CompletionsAreBackdated()
    {
        await SeedChoresAsync();

        await SeedData.SeedDevEquityDataAsync(_dbFactory.Object, HouseholdId);

        await using var context = new ApplicationDbContext(_options);
        var completions = await context.ChoreCompletions
            .Where(cc => cc.HouseholdId == HouseholdId)
            .ToListAsync();

        completions.Should().OnlyContain(cc => cc.CompletedAt < DateTime.UtcNow.AddSeconds(1),
            "all equity-seed completions must be in the past");
    }

    // -------------------------------------------------------------------------
    // Idempotency
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SeedDevEquityDataAsync_IsIdempotent_SecondCallDoesNotDuplicate()
    {
        await SeedChoresAsync();

        await SeedData.SeedDevEquityDataAsync(_dbFactory.Object, HouseholdId);

        int memberCount, completionCount;
        await using (var context = new ApplicationDbContext(_options))
        {
            memberCount = await context.Users.CountAsync(u => u.HouseholdId == HouseholdId);
            completionCount = await context.ChoreCompletions.CountAsync(cc => cc.HouseholdId == HouseholdId);
        }

        // Second call — must be a no-op.
        await SeedData.SeedDevEquityDataAsync(_dbFactory.Object, HouseholdId);

        await using var verify = new ApplicationDbContext(_options);
        (await verify.Users.CountAsync(u => u.HouseholdId == HouseholdId)).Should().Be(memberCount);
        (await verify.ChoreCompletions.CountAsync(cc => cc.HouseholdId == HouseholdId)).Should().Be(completionCount);
    }

    // -------------------------------------------------------------------------
    // No-chores graceful bail
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SeedDevEquityDataAsync_NoChores_BailsGracefully()
    {
        // Do NOT seed chores — equity seed should add users but not crash when there are no chores.
        await SeedData.SeedDevEquityDataAsync(_dbFactory.Object, HouseholdId);

        // No exception — and no completion rows (nothing to attach to).
        await using var context = new ApplicationDbContext(_options);
        (await context.ChoreCompletions.AnyAsync(cc => cc.HouseholdId == HouseholdId)).Should().BeFalse(
            "without chore rows the equity seed must not insert orphaned completions");
    }

    // -------------------------------------------------------------------------
    // MN11: prod path untouched
    // -------------------------------------------------------------------------

    [Fact]
    public async Task SeedChoresAndRoomsAsync_DoesNotCallEquitySeed_ProdPathUnchanged()
    {
        // SeedChoresAndRoomsAsync (called from SetupService.CreateHouseholdAsync in prod) must NOT
        // add extra members. After it runs, the household should still have exactly one member.
        await SeedData.SeedChoresAndRoomsAsync(_dbFactory.Object, HouseholdId);

        await using var context = new ApplicationDbContext(_options);
        var memberCount = await context.Users.CountAsync(u => u.HouseholdId == HouseholdId);
        memberCount.Should().Be(1, "SeedChoresAndRoomsAsync must not add synthetic members (MN11)");
    }
}
