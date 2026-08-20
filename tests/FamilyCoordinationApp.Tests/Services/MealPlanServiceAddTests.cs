using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// AddMealAsync attribution tests (F1 — the mealsPlanned planning lane). A NEW entry is stamped with
/// <see cref="MealPlanEntry.CreatedByUserId"/>; the duplicate-fold path (same recipe/custom meal already
/// in the slot) must NEVER reassign the original creator — folding is an update, and it touches
/// <see cref="MealPlanEntry.UpdatedByUserId"/> only. Reassignment would silently move planning credit
/// between members.
/// </summary>
public class MealPlanServiceAddTests : IDisposable
{
    private static readonly DateOnly WeekStart = new(2026, 7, 6);

    private readonly ApplicationDbContext _context;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly MealPlanService _service;

    public MealPlanServiceAddTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(_options);

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        _service = new MealPlanService(dbFactoryMock.Object, new Mock<ILogger<MealPlanService>>().Object);

        SeedTestData();
    }

    private void SeedTestData()
    {
        _context.Households.Add(new Household { Id = 1, Name = "Smith Family" });

        _context.Users.AddRange(
            new User { Id = 1, HouseholdId = 1, Email = "a@b.com", DisplayName = "Alice" },
            new User { Id = 2, HouseholdId = 1, Email = "b@b.com", DisplayName = "Bob" });

        _context.Recipes.Add(new Recipe
        {
            HouseholdId = 1,
            RecipeId = 1,
            Name = "Tacos",
            RecipeType = RecipeType.Main,
            CreatedAt = DateTime.UtcNow,
        });

        _context.MealPlans.Add(new MealPlan
        {
            HouseholdId = 1,
            MealPlanId = 1,
            WeekStartDate = WeekStart,
            CreatedAt = DateTime.UtcNow,
        });

        // Bob's existing Wednesday-dinner Tacos — the duplicate-fold target.
        _context.MealPlanEntries.Add(new MealPlanEntry
        {
            HouseholdId = 1,
            MealPlanId = 1,
            EntryId = 1,
            Date = WeekStart.AddDays(2),
            MealType = MealType.Dinner,
            RecipeId = 1,
            CreatedByUserId = 2,
        });

        _context.SaveChanges();
    }

    [Fact]
    public async Task AddMealAsync_NewEntry_StampsCreatedByUserId()
    {
        var entry = await _service.AddMealAsync(
            householdId: 1, date: WeekStart, mealType: MealType.Dinner,
            recipeId: 1, customMealName: null, notes: null, userId: 1);

        entry.CreatedByUserId.Should().Be(1, "the creating user earns the mealsPlanned planning credit");
    }

    [Fact]
    public async Task AddMealAsync_DuplicateFold_DoesNotReassignCreator()
    {
        // Alice adds the same recipe into Bob's existing slot → folds into entry 1.
        var entry = await _service.AddMealAsync(
            householdId: 1, date: WeekStart.AddDays(2), mealType: MealType.Dinner,
            recipeId: 1, customMealName: null, notes: "extra salsa", userId: 1);

        entry.EntryId.Should().Be(1, "the add folds into the existing duplicate entry");
        entry.CreatedByUserId.Should().Be(2, "folding is an update — the original creator keeps the planning credit");
        entry.UpdatedByUserId.Should().Be(1, "the folding user is recorded as the updater");
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }
}
