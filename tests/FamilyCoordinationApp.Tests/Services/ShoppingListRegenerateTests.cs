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

    /// <summary>
    /// The end-to-end council finding: a quantity edit must survive the SERVICE persistence hop
    /// (UpdateItemWithConcurrencyAsync's field copy dropped QuantityDelta) and then be re-applied by
    /// regenerate — the full PATCH → persist → regenerate path, not just the pure delta math.
    /// </summary>
    [Fact]
    public async Task QuantityEdit_SurvivesPersistence_AndRegenerate()
    {
        var dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));
        var service = new ShoppingListService(
            dbFactoryMock.Object, new Mock<ILogger<ShoppingListService>>().Object);

        // The eggs row (generated, no delta yet): the user edits 2 → 5, PatchItem-style.
        var eggs = (await ItemsAsync()).Single(i => i.Name == "eggs");
        eggs.Quantity = 5m;
        eggs.QuantityDelta = 3m; // what ShoppingListEndpoints.ComputeQuantityDelta produces for 2 → 5

        var (success, _, _) = await service.UpdateItemWithConcurrencyAsync(eggs);
        success.Should().BeTrue();

        (await ItemsAsync()).Single(i => i.Name == "eggs").QuantityDelta.Should().Be(3m,
            "the persistence hop must not drop the delta — its field copy is a whitelist");

        await _generator.RegenerateShoppingListAsync(1, 50);

        var regenerated = (await ItemsAsync()).Single(i => i.Name == "eggs");
        regenerated.Quantity.Should().Be(5m, "fresh consolidated 2 + carried delta 3");
        regenerated.QuantityDelta.Should().Be(3m);
    }

    /// <summary>
    /// Carry-over identity is the CONSOLIDATOR'S — (normalized name, category) — so two same-name rows
    /// in different categories each keep their own state instead of collapsing onto one.
    /// </summary>
    [Fact]
    public async Task Regenerate_CarriesStatePerCategory_WhenNamesCollide()
    {
        await using (var ctx = new ApplicationDbContext(_options))
        {
            ctx.RecipeIngredients.AddRange(
                new RecipeIngredient
                {
                    HouseholdId = 1, RecipeId = 10, IngredientId = 3,
                    Name = "ginger", Quantity = 1m, Unit = null, Category = "Produce"
                },
                new RecipeIngredient
                {
                    HouseholdId = 1, RecipeId = 10, IngredientId = 4,
                    Name = "ginger", Quantity = 1m, Unit = null, Category = "Spices"
                });
            ctx.ShoppingListItems.AddRange(
                new ShoppingListItem
                {
                    HouseholdId = 1, ShoppingListId = 50, ItemId = 5,
                    Name = "ginger", Quantity = 1m, Unit = null, Category = "Produce",
                    IsManuallyAdded = false, IsChecked = true, CheckedAt = CheckedInstant, SortOrder = 4
                },
                new ShoppingListItem
                {
                    HouseholdId = 1, ShoppingListId = 50, ItemId = 6,
                    Name = "ginger", Quantity = 1m, Unit = null, Category = "Spices",
                    IsManuallyAdded = false, QuantityDelta = 2m, SortOrder = 5
                });
            await ctx.SaveChangesAsync();
        }

        await _generator.RegenerateShoppingListAsync(1, 50);

        var items = await ItemsAsync();
        var produceGinger = items.Single(i => i.Name == "ginger" && i.Category == "Produce");
        produceGinger.IsChecked.Should().BeTrue("Produce ginger was checked — its state must not leak to Spices");
        produceGinger.QuantityDelta.Should().BeNull();

        var spicesGinger = items.Single(i => i.Name == "ginger" && i.Category == "Spices");
        spicesGinger.IsChecked.Should().BeFalse();
        spicesGinger.QuantityDelta.Should().Be(2m);
        spicesGinger.Quantity.Should().Be(3m, "fresh 1 + carried delta 2");
    }

    /// <summary>Regenerate honours entry servings the same way generation does, then re-applies the delta.</summary>
    [Fact]
    public async Task Regenerate_ScalesByEntryServings_ThenAppliesDelta()
    {
        await using (var ctx = new ApplicationDbContext(_options))
        {
            var recipe = await ctx.Recipes.SingleAsync(r => r.HouseholdId == 1 && r.RecipeId == 10);
            recipe.Servings = 4;
            var entry = await ctx.MealPlanEntries.SingleAsync(e => e.HouseholdId == 1 && e.MealPlanId == 900);
            entry.Servings = 8; // ×2 against the recipe's own yield
            await ctx.SaveChangesAsync();
        }

        await _generator.RegenerateShoppingListAsync(1, 50);

        var flour = (await ItemsAsync()).Single(i => i.Name == "flour");
        flour.Quantity.Should().Be(5m, "scaled 2 cup × 2 = 4, + carried delta 1");
    }

    /// <summary>A carried negative edit larger than the fresh quantity floors at zero, never negative.</summary>
    [Fact]
    public async Task Regenerate_FloorsQuantityAtZero()
    {
        await using (var ctx = new ApplicationDbContext(_options))
        {
            var flour = await ctx.ShoppingListItems
                .SingleAsync(i => i.HouseholdId == 1 && i.ShoppingListId == 50 && i.Name == "flour");
            flour.QuantityDelta = -10m;
            await ctx.SaveChangesAsync();
        }

        await _generator.RegenerateShoppingListAsync(1, 50);

        (await ItemsAsync()).Single(i => i.Name == "flour").Quantity.Should().Be(0m);
    }

    public void Dispose() => _seedContext.Dispose();
}
