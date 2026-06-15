using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Endpoints;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// WP-04 (Phase 15) backend tests for the <c>PATCH /api/chores/me/capacity</c> setter core
/// (<see cref="ChoresEndpoints.ApplyCapacityAsync"/>): persists a valid tier per-user, echoes it (200),
/// rejects a non-null unknown tier without mutating (→ 400), and is scoped to the caller only (D2/MN5).
/// Class name contains "Capacity" so the WP verification filter (<c>--filter "FullyQualifiedName~Capacity"</c>)
/// selects it. <c>null</c> is the pre-migration default (treated as Full) — there is no client clear-to-null
/// path, so it is not exercised here.
/// </summary>
public class CapacityAllowlistTests
{
    [Theory]
    [InlineData("Full")]
    [InlineData("Reduced")]
    [InlineData("Minimal")]
    public async Task ApplyCapacity_PersistsValidTier_AndEchoes_ForCaller(string tier)
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, tier: null);

        var (outcome, normalized) =
            await ChoresEndpoints.ApplyCapacityAsync(factory, userId: 1, tier, CancellationToken.None);

        outcome.Should().Be(ChoresEndpoints.CapacityOutcome.Ok);
        normalized.Should().Be(tier);
        ReadTier(options, userId: 1).Should().Be(tier);
    }

    [Fact]
    public async Task ApplyCapacity_PersistsViaCanonicalConstants()
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, tier: null);

        var (outcome, normalized) =
            await ChoresEndpoints.ApplyCapacityAsync(factory, userId: 1, CapacityTier.Reduced, CancellationToken.None);

        outcome.Should().Be(ChoresEndpoints.CapacityOutcome.Ok);
        normalized.Should().Be(CapacityTier.Reduced);
        ReadTier(options, userId: 1).Should().Be(CapacityTier.Reduced);
    }

    [Fact]
    public async Task ApplyCapacity_RejectsUnknownTier_WithoutMutating()
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, tier: CapacityTier.Full);

        var (outcome, normalized) =
            await ChoresEndpoints.ApplyCapacityAsync(factory, userId: 1, "Halftime", CancellationToken.None);

        outcome.Should().Be(ChoresEndpoints.CapacityOutcome.InvalidTier);
        normalized.Should().BeNull();
        ReadTier(options, userId: 1).Should().Be(CapacityTier.Full); // unchanged, never coerced
    }

    [Fact]
    public async Task ApplyCapacity_RejectsWrongCasing_WithoutCoercing()
    {
        // The allowlist is exact-match canonical (no ad-hoc casings, D2/A6).
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, tier: null);

        var (outcome, _) =
            await ChoresEndpoints.ApplyCapacityAsync(factory, userId: 1, "reduced", CancellationToken.None);

        outcome.Should().Be(ChoresEndpoints.CapacityOutcome.InvalidTier);
        ReadTier(options, userId: 1).Should().BeNull(); // unchanged
    }

    [Fact]
    public async Task ApplyCapacity_IsScopedToCaller_DoesNotTouchOtherUser()
    {
        var (factory, _) = NewFactory(out var options);
        SeedUser(options, userId: 1, householdId: 1, tier: null);
        SeedUser(options, userId: 2, householdId: 1, tier: CapacityTier.Full);

        await ChoresEndpoints.ApplyCapacityAsync(factory, userId: 1, CapacityTier.Minimal, CancellationToken.None);

        ReadTier(options, userId: 1).Should().Be(CapacityTier.Minimal);
        ReadTier(options, userId: 2).Should().Be(CapacityTier.Full); // other caller untouched (MN5)
    }

    // ─── Helpers ────────────────────────────────────────────────────────────────────

    private static (IDbContextFactory<ApplicationDbContext> Factory, DbContextOptions<ApplicationDbContext> Options)
        NewFactory(out DbContextOptions<ApplicationDbContext> options)
    {
        options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var captured = options;
        var mock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        mock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(captured));
        return (mock.Object, options);
    }

    private static void SeedUser(DbContextOptions<ApplicationDbContext> options, int userId, int householdId, string? tier)
    {
        using var ctx = new ApplicationDbContext(options);
        if (!ctx.Households.Any(h => h.Id == householdId))
        {
            ctx.Households.Add(new Household { Id = householdId, Name = $"H{householdId}" });
        }
        ctx.Users.Add(new User
        {
            Id = userId,
            HouseholdId = householdId,
            Email = $"u{userId}@b.com",
            DisplayName = $"User{userId}",
            PhysicalCapacityTier = tier
        });
        ctx.SaveChanges();
    }

    private static string? ReadTier(DbContextOptions<ApplicationDbContext> options, int userId)
    {
        using var ctx = new ApplicationDbContext(options);
        return ctx.Users.Single(u => u.Id == userId).PhysicalCapacityTier;
    }
}
