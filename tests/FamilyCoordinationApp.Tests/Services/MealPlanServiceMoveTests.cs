using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// MoveMealAsync (drag-to-assign): same-week slot move semantics — happy path, household isolation
/// (M1), the same-week guard (a plan owns exactly one week), and the duplicate-in-target-slot guard
/// (mirrors AddMealAsync's dedupe).
/// </summary>
public class MealPlanServiceMoveTests : IDisposable
{
    // Monday of the seeded week.
    private static readonly DateOnly WeekStart = new(2026, 7, 6);

    private readonly ApplicationDbContext _context;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly MealPlanService _service;

    public MealPlanServiceMoveTests()
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
        _context.Households.AddRange(
            new Household { Id = 1, Name = "Smith Family" },
            new Household { Id = 2, Name = "Jones Family" });

        _context.Users.Add(new User { Id = 1, HouseholdId = 1, Email = "a@b.com", DisplayName = "Alice" });

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

        _context.MealPlanEntries.AddRange(
            // The entry the tests move: Monday dinner, recipe meal.
            new MealPlanEntry
            {
                HouseholdId = 1,
                MealPlanId = 1,
                EntryId = 1,
                Date = WeekStart,
                MealType = MealType.Dinner,
                RecipeId = 1,
            },
            // Same recipe already sits in Wednesday dinner — the duplicate-guard target.
            new MealPlanEntry
            {
                HouseholdId = 1,
                MealPlanId = 1,
                EntryId = 2,
                Date = WeekStart.AddDays(2),
                MealType = MealType.Dinner,
                RecipeId = 1,
            },
            // A custom meal on Monday lunch (custom-name dedupe check).
            new MealPlanEntry
            {
                HouseholdId = 1,
                MealPlanId = 1,
                EntryId = 3,
                Date = WeekStart,
                MealType = MealType.Lunch,
                CustomMealName = "Leftovers",
            });

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
        GC.SuppressFinalize(this);
    }

    [Fact]
    public async Task MoveMeal_ToAnotherSameWeekSlot_UpdatesDateAndMealType()
    {
        var newDate = WeekStart.AddDays(3); // Thursday
        var moved = await _service.MoveMealAsync(1, 1, 1, newDate, MealType.Lunch, userId: 1);

        moved.Date.Should().Be(newDate);
        moved.MealType.Should().Be(MealType.Lunch);
        moved.UpdatedByUserId.Should().Be(1);
        moved.UpdatedAt.Should().NotBeNull();
        moved.Recipe.Should().NotBeNull("the Recipe nav must be loaded for the response projection");

        await using var verify = new ApplicationDbContext(_options);
        var persisted = await verify.MealPlanEntries
            .SingleAsync(e => e.HouseholdId == 1 && e.MealPlanId == 1 && e.EntryId == 1);
        persisted.Date.Should().Be(newDate);
        persisted.MealType.Should().Be(MealType.Lunch);

        var plan = await verify.MealPlans.SingleAsync(mp => mp.HouseholdId == 1 && mp.MealPlanId == 1);
        plan.UpdatedAt.Should().NotBeNull("the move must bump the plan timestamp for polling");
    }

    [Fact]
    public async Task MoveMeal_MissingEntry_Throws()
    {
        var act = () => _service.MoveMealAsync(1, 1, 999, WeekStart, MealType.Breakfast);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task MoveMeal_CrossHousehold_IsNotFound()
    {
        // Household 2 asking for household 1's entry — the scoped lookup must not see it (M1).
        var act = () => _service.MoveMealAsync(2, 1, 1, WeekStart, MealType.Breakfast);
        await act.Should().ThrowAsync<InvalidOperationException>();

        await using var verify = new ApplicationDbContext(_options);
        var untouched = await verify.MealPlanEntries
            .SingleAsync(e => e.HouseholdId == 1 && e.MealPlanId == 1 && e.EntryId == 1);
        untouched.Date.Should().Be(WeekStart);
        untouched.MealType.Should().Be(MealType.Dinner);
    }

    [Fact]
    public async Task MoveMeal_DateOutsideThePlansWeek_Throws()
    {
        var nextMonday = WeekStart.AddDays(7);
        var act = () => _service.MoveMealAsync(1, 1, 1, nextMonday, MealType.Dinner);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*outside this meal plan's week*");
    }

    [Fact]
    public async Task MoveMeal_SameRecipeAlreadyInTargetSlot_Throws()
    {
        // Entry 1 (Tacos) → Wednesday dinner, where entry 2 already plans Tacos.
        var act = () => _service.MoveMealAsync(1, 1, 1, WeekStart.AddDays(2), MealType.Dinner);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*already planned*");
    }

    [Fact]
    public async Task MoveMeal_SameCustomMealAlreadyInTargetSlot_Throws()
    {
        // A second "Leftovers" on Tuesday dinner, then move Monday-lunch "Leftovers" onto it.
        _context.MealPlanEntries.Add(new MealPlanEntry
        {
            HouseholdId = 1,
            MealPlanId = 1,
            EntryId = 4,
            Date = WeekStart.AddDays(1),
            MealType = MealType.Dinner,
            CustomMealName = "Leftovers",
        });
        _context.SaveChanges();

        var act = () => _service.MoveMealAsync(1, 1, 3, WeekStart.AddDays(1), MealType.Dinner);
        await act.Should().ThrowAsync<ArgumentException>()
            .WithMessage("*already planned*");
    }

    [Fact]
    public async Task MoveMeal_DifferentMealInTargetSlot_IsAllowed()
    {
        // Moving the custom meal onto Monday dinner (which holds a RECIPE meal) is fine —
        // the dedupe only blocks the SAME recipe/custom name, not co-located different meals.
        var moved = await _service.MoveMealAsync(1, 1, 3, WeekStart, MealType.Dinner);

        moved.MealType.Should().Be(MealType.Dinner);
        await using var verify = new ApplicationDbContext(_options);
        var slotEntries = await verify.MealPlanEntries
            .Where(e => e.HouseholdId == 1 && e.Date == WeekStart && e.MealType == MealType.Dinner)
            .CountAsync();
        slotEntries.Should().Be(2);
    }
}
