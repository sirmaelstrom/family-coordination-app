using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

/// <summary>
/// The regenerate rework (quest f63bb90a, spec-lite §Regenerate): carry-over by normalized-name match
/// (IsChecked/CheckedAt/SortOrder), re-applied QuantityDelta, preserved manual items, and a SINGLE
/// SaveChanges — the pre-rework implementation deleted and re-added through per-item service calls,
/// wiping checked state and saving once per item.
/// </summary>
public class ShoppingListRegenerateTests : IDisposable
{
    /// <summary>Counts SaveChanges calls — the atomicity claim is "one save", assert it, don't narrate it.</summary>
    private sealed class SaveCountingInterceptor : SaveChangesInterceptor
    {
        public int Saves;

        public override InterceptionResult<int> SavingChanges(
            DbContextEventData eventData, InterceptionResult<int> result)
        {
            Interlocked.Increment(ref Saves);
            return base.SavingChanges(eventData, result);
        }

        public override ValueTask<InterceptionResult<int>> SavingChangesAsync(
            DbContextEventData eventData, InterceptionResult<int> result, CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref Saves);
            return base.SavingChangesAsync(eventData, result, cancellationToken);
        }
    }

    private readonly SaveCountingInterceptor _saveCounter = new();
    private readonly ApplicationDbContext _seedContext;
    private readonly DbContextOptions<ApplicationDbContext> _options;
    private readonly ShoppingListGenerator _generator;

    private static readonly DateTime CheckedInstant = new(2026, 6, 20, 14, 30, 0, DateTimeKind.Utc);

    public ShoppingListRegenerateTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .AddInterceptors(_saveCounter)
            .Options;

        _seedContext = new ApplicationDbContext(_options);

        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));

        _generator = new ShoppingListGenerator(
            dbFactoryMock.Object,
            new Mock<IShoppingListService>().Object,
            new UnitConverter(),
            new Mock<ILogger<ShoppingListGenerator>>().Object);

        SeedAsync().GetAwaiter().GetResult();
        _saveCounter.Saves = 0; // count only what regenerate does
    }

    private async Task SeedAsync()
    {
        _seedContext.Households.Add(new Household { Id = 1, Name = "Smith Family" });

        var recipe = new Recipe { HouseholdId = 1, RecipeId = 10, Name = "Pancakes", CreatedAt = DateTime.UtcNow };
        _seedContext.Recipes.Add(recipe);
        _seedContext.RecipeIngredients.AddRange(
            new RecipeIngredient
            {
                HouseholdId = 1, RecipeId = 10, IngredientId = 1,
                Name = "flour", Quantity = 2m, Unit = "cup", Category = "Baking", Recipe = recipe
            },
            new RecipeIngredient
            {
                HouseholdId = 1, RecipeId = 10, IngredientId = 2,
                Name = "eggs", Quantity = 2m, Unit = null, Category = "Dairy", Recipe = recipe
            });

        var plan = new MealPlan { HouseholdId = 1, MealPlanId = 900, WeekStartDate = new DateOnly(2026, 6, 15) };
        _seedContext.MealPlans.Add(plan);
        _seedContext.MealPlanEntries.Add(new MealPlanEntry
        {
            HouseholdId = 1, MealPlanId = 900, EntryId = 1,
            Date = new DateOnly(2026, 6, 15), MealType = MealType.Dinner, RecipeId = 10
        });

        _seedContext.ShoppingLists.Add(new ShoppingList
        {
            HouseholdId = 1, ShoppingListId = 50, Name = "Week", MealPlanId = 900,
            CreatedAt = DateTime.UtcNow, IsArchived = false
        });
        _seedContext.ShoppingListItems.AddRange(
            // Generated, edited (+1 over the generator's 2) and checked, custom sort position.
            new ShoppingListItem
            {
                HouseholdId = 1, ShoppingListId = 50, ItemId = 1,
                Name = "flour", Quantity = 3m, Unit = "cup", Category = "Baking",
                IsManuallyAdded = false, QuantityDelta = 1m,
                IsChecked = true, CheckedAt = CheckedInstant, SortOrder = 7
            },
            // Generated, untouched.
            new ShoppingListItem
            {
                HouseholdId = 1, ShoppingListId = 50, ItemId = 2,
                Name = "eggs", Quantity = 2m, Unit = null, Category = "Dairy",
                IsManuallyAdded = false, SortOrder = 2
            },
            // Generated from an ingredient the plan no longer contains — must disappear.
            new ShoppingListItem
            {
                HouseholdId = 1, ShoppingListId = 50, ItemId = 3,
                Name = "unicorn dust", Quantity = 1m, Unit = null, Category = "Pantry",
                IsManuallyAdded = false, SortOrder = 9
            },
            // Manual, checked — must survive regenerate byte-for-byte.
            new ShoppingListItem
            {
                HouseholdId = 1, ShoppingListId = 50, ItemId = 4,
                Name = "duct tape", Quantity = 1m, Unit = null, Category = "Household",
                IsManuallyAdded = true, IsChecked = true, CheckedAt = CheckedInstant, SortOrder = 1
            });

        // An unlinked list for the gate test.
        _seedContext.ShoppingLists.Add(new ShoppingList
        {
            HouseholdId = 1, ShoppingListId = 51, Name = "Ad hoc", MealPlanId = null,
            CreatedAt = DateTime.UtcNow, IsArchived = false
        });

        await _seedContext.SaveChangesAsync();
    }

    private async Task<List<ShoppingListItem>> ItemsAsync()
    {
        await using var ctx = new ApplicationDbContext(_options);
        return await ctx.ShoppingListItems
            .Where(i => i.HouseholdId == 1 && i.ShoppingListId == 50)
            .ToListAsync();
    }

    [Fact]
    public async Task Regenerate_CarriesCheckedStateSortOrderAndDelta_ByNormalizedName()
    {
        await _generator.RegenerateShoppingListAsync(1, 50);

        var items = await ItemsAsync();
        var flour = items.Single(i => i.Name == "flour");
        flour.Quantity.Should().Be(3m, "fresh consolidated 2 cup + carried delta 1");
        flour.QuantityDelta.Should().Be(1m);
        flour.IsChecked.Should().BeTrue("the household already bought the flour — regenerate must not uncheck it");
        flour.CheckedAt.Should().Be(CheckedInstant);
        flour.SortOrder.Should().Be(7);
        flour.IsManuallyAdded.Should().BeFalse();

        var eggs = items.Single(i => i.Name == "eggs");
        eggs.IsChecked.Should().BeFalse();
        eggs.SortOrder.Should().Be(2);
    }

    [Fact]
    public async Task Regenerate_DropsRowsThePlanNoLongerProduces()
    {
        await _generator.RegenerateShoppingListAsync(1, 50);

        (await ItemsAsync()).Should().NotContain(i => i.Name == "unicorn dust");
    }

    [Fact]
    public async Task Regenerate_PreservesManualItemsUntouched()
    {
        await _generator.RegenerateShoppingListAsync(1, 50);

        var tape = (await ItemsAsync()).Single(i => i.Name == "duct tape");
        tape.IsManuallyAdded.Should().BeTrue();
        tape.IsChecked.Should().BeTrue();
        tape.CheckedAt.Should().Be(CheckedInstant);
        tape.ItemId.Should().Be(4, "manual rows are not rebuilt");
    }

    [Fact]
    public async Task Regenerate_SavesExactlyOnce()
    {
        await _generator.RegenerateShoppingListAsync(1, 50);

        _saveCounter.Saves.Should().Be(1,
            "the rebuild must be atomic — the pre-rework implementation saved once per item through service calls");
    }

    [Fact]
    public async Task Regenerate_UnlinkedList_Throws()
    {
        var act = () => _generator.RegenerateShoppingListAsync(1, 51);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not linked*");
    }

    [Fact]
    public async Task Regenerate_RespectsHouseholdIsolation()
    {
        var act = () => _generator.RegenerateShoppingListAsync(2, 50);

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("*not found*");
    }

    public void Dispose() => _seedContext.Dispose();
}
