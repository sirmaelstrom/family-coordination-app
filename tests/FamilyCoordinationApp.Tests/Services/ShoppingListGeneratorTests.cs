using FluentAssertions;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Moq;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Tests.Services;

public class ShoppingListGeneratorTests : IDisposable
{
    private readonly ApplicationDbContext _context;
    private readonly Mock<IDbContextFactory<ApplicationDbContext>> _dbFactoryMock;
    private readonly Mock<IShoppingListService> _shoppingListServiceMock;
    private readonly Mock<ILogger<ShoppingListGenerator>> _loggerMock;
    private readonly UnitConverter _unitConverter;
    private readonly ShoppingListGenerator _generator;
    private readonly DbContextOptions<ApplicationDbContext> _options;

    public ShoppingListGeneratorTests()
    {
        _options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _context = new ApplicationDbContext(_options);
        _dbFactoryMock = new Mock<IDbContextFactory<ApplicationDbContext>>();
        _dbFactoryMock.Setup(f => f.CreateDbContextAsync(It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => new ApplicationDbContext(_options));
        _shoppingListServiceMock = new Mock<IShoppingListService>();
        _loggerMock = new Mock<ILogger<ShoppingListGenerator>>();
        _unitConverter = new UnitConverter();

        _generator = new ShoppingListGenerator(
            _dbFactoryMock.Object,
            _shoppingListServiceMock.Object,
            _unitConverter,
            _loggerMock.Object);

        SeedTestData();
    }

    private void SeedTestData()
    {
        // Create households
        _context.Households.AddRange(
            new Household { Id = 1, Name = "Smith Family" },
            new Household { Id = 2, Name = "Jones Family" }
        );

        // Create recipes for household 1
        var pancakeRecipe = new Recipe
        {
            HouseholdId = 1,
            RecipeId = 1,
            Name = "Pancakes",
            CreatedAt = DateTime.UtcNow
        };

        var frenchToastRecipe = new Recipe
        {
            HouseholdId = 1,
            RecipeId = 2,
            Name = "French Toast",
            CreatedAt = DateTime.UtcNow
        };

        var steakRecipe = new Recipe
        {
            HouseholdId = 1,
            RecipeId = 3,
            Name = "Steak Dinner",
            CreatedAt = DateTime.UtcNow
        };

        _context.Recipes.AddRange(pancakeRecipe, frenchToastRecipe, steakRecipe);

        // Ingredients for Pancakes (has flour and milk)
        _context.RecipeIngredients.AddRange(
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 1,
                IngredientId = 1,
                Name = "flour",
                Quantity = 2,
                Unit = "cups",
                Category = "Baking",
                Recipe = pancakeRecipe
            },
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 1,
                IngredientId = 2,
                Name = "milk",
                Quantity = 1,
                Unit = "cup",
                Category = "Dairy",
                Recipe = pancakeRecipe
            },
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 1,
                IngredientId = 3,
                Name = "eggs",
                Quantity = 2,
                Unit = null,
                Category = "Dairy",
                Recipe = pancakeRecipe
            }
        );

        // Ingredients for French Toast (also has milk - should consolidate)
        _context.RecipeIngredients.AddRange(
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 2,
                IngredientId = 1,
                Name = "milk",
                Quantity = 0.5m,
                Unit = "cup",
                Category = "Dairy",
                Recipe = frenchToastRecipe
            },
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 2,
                IngredientId = 2,
                Name = "eggs",
                Quantity = 3,
                Unit = null,
                Category = "Dairy",
                Recipe = frenchToastRecipe
            },
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 2,
                IngredientId = 3,
                Name = "bread",
                Quantity = 4,
                Unit = "slices",
                Category = "Bakery",
                Recipe = frenchToastRecipe
            }
        );

        // Ingredients for Steak (has butter in different unit - tests conversion)
        _context.RecipeIngredients.AddRange(
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 3,
                IngredientId = 1,
                Name = "butter",
                Quantity = 2,
                Unit = "tbsp",
                Category = "Dairy",
                Recipe = steakRecipe
            },
            new RecipeIngredient
            {
                HouseholdId = 1,
                RecipeId = 3,
                IngredientId = 2,
                Name = "steak",
                Quantity = 1,
                Unit = "lb",
                Category = "Meat",
                Recipe = steakRecipe
            }
        );

        // Create recipe for household 2 (different household - for isolation test)
        var household2Recipe = new Recipe
        {
            HouseholdId = 2,
            RecipeId = 1,
            Name = "Household 2 Recipe",
            CreatedAt = DateTime.UtcNow
        };

        _context.Recipes.Add(household2Recipe);

        _context.RecipeIngredients.Add(
            new RecipeIngredient
            {
                HouseholdId = 2,
                RecipeId = 1,
                IngredientId = 1,
                Name = "milk",
                Quantity = 5,
                Unit = "cups",
                Category = "Dairy",
                Recipe = household2Recipe
            }
        );

        // Create meal plan for household 1
        var mealPlan = new MealPlan
        {
            HouseholdId = 1,
            MealPlanId = 1,
            WeekStartDate = DateOnly.FromDateTime(DateTime.Today),
            CreatedAt = DateTime.UtcNow
        };

        _context.MealPlans.Add(mealPlan);

        // Create meal plan entries
        _context.MealPlanEntries.AddRange(
            new MealPlanEntry
            {
                HouseholdId = 1,
                MealPlanId = 1,
                EntryId = 1,
                Date = DateOnly.FromDateTime(DateTime.Today),
                MealType = MealType.Breakfast,
                RecipeId = 1, // Pancakes
                Recipe = pancakeRecipe
            },
            new MealPlanEntry
            {
                HouseholdId = 1,
                MealPlanId = 1,
                EntryId = 2,
                Date = DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
                MealType = MealType.Breakfast,
                RecipeId = 2, // French Toast
                Recipe = frenchToastRecipe
            },
            new MealPlanEntry
            {
                HouseholdId = 1,
                MealPlanId = 1,
                EntryId = 3,
                Date = DateOnly.FromDateTime(DateTime.Today.AddDays(2)),
                MealType = MealType.Dinner,
                RecipeId = 3, // Steak Dinner
                Recipe = steakRecipe
            }
        );

        // Create meal plan for household 2
        var mealPlan2 = new MealPlan
        {
            HouseholdId = 2,
            MealPlanId = 1,
            WeekStartDate = DateOnly.FromDateTime(DateTime.Today),
            CreatedAt = DateTime.UtcNow
        };

        _context.MealPlans.Add(mealPlan2);

        _context.MealPlanEntries.Add(
            new MealPlanEntry
            {
                HouseholdId = 2,
                MealPlanId = 1,
                EntryId = 1,
                Date = DateOnly.FromDateTime(DateTime.Today),
                MealType = MealType.Lunch,
                RecipeId = 1,
                Recipe = household2Recipe
            }
        );

        _context.SaveChanges();
    }

    public void Dispose()
    {
        _context.Dispose();
    }

    [Fact]
    public async Task ConsolidateIngredientsAsync_ConsolidatesDuplicateIngredients()
    {
        // Arrange - Get ingredients from both Pancakes and French Toast (both have milk)
        var ingredients = _context.RecipeIngredients
            .Include(ri => ri.Recipe)
            .Where(ri => ri.HouseholdId == 1 && ri.Name == "milk")
            .ToList();

        ingredients.Should().HaveCount(2); // Both recipes have milk

        // Act
        var results = await _generator.ConsolidateIngredientsAsync(ingredients, autoConsolidate: true);

        // Assert
        results.Should().HaveCount(1); // Should be consolidated into one entry
        var milkResult = results.First();
        milkResult.Name.Should().Be("milk");
        milkResult.Quantity.Should().Be(1.5m); // 1 cup + 0.5 cup = 1.5 cups
        milkResult.Unit.Should().Be("cup");
        milkResult.SourceRecipes.Should().Contain("Pancakes");
        milkResult.SourceRecipes.Should().Contain("French Toast");
    }

    [Fact]
    public async Task ConsolidateIngredientsAsync_HandlesUnitConversion()
    {
        // Arrange - Create ingredients with different but convertible units
        var recipe = new Recipe { HouseholdId = 1, RecipeId = 100, Name = "Test Recipe" };
        var ingredients = new List<RecipeIngredient>
        {
            new()
            {
                HouseholdId = 1,
                RecipeId = 100,
                IngredientId = 1,
                Name = "milk",
                Quantity = 1,
                Unit = "cup",
                Category = "Dairy",
                Recipe = recipe
            },
            new()
            {
                HouseholdId = 1,
                RecipeId = 100,
                IngredientId = 2,
                Name = "milk",
                Quantity = 8,
                Unit = "tbsp", // 8 tbsp = 0.5 cup
                Category = "Dairy",
                Recipe = recipe
            }
        };

        // Act
        var results = await _generator.ConsolidateIngredientsAsync(ingredients, autoConsolidate: true);

        // Assert
        results.Should().HaveCount(1);
        var milkResult = results.First();
        milkResult.Quantity.Should().Be(1.5m); // 1 cup + 0.5 cup
        milkResult.OriginalUnits.Should().NotBeNull(); // Should track original units
    }

    [Fact]
    public async Task ConsolidateIngredientsAsync_KeepsSeparateForIncompatibleUnits()
    {
        // Arrange - Create ingredients with incompatible units (weight vs volume)
        var recipe = new Recipe { HouseholdId = 1, RecipeId = 100, Name = "Test Recipe" };
        var ingredients = new List<RecipeIngredient>
        {
            new()
            {
                HouseholdId = 1,
                RecipeId = 100,
                IngredientId = 1,
                Name = "flour",
                Quantity = 2,
                Unit = "cups",
                Category = "Baking",
                Recipe = recipe
            },
            new()
            {
                HouseholdId = 1,
                RecipeId = 100,
                IngredientId = 2,
                Name = "flour",
                Quantity = 500,
                Unit = "g", // grams - weight, not volume
                Category = "Baking",
                Recipe = recipe
            }
        };

        // Act
        var results = await _generator.ConsolidateIngredientsAsync(ingredients, autoConsolidate: true);

        // Assert - Should keep separate because cups (volume) and grams (weight) can't be converted
        results.Should().HaveCount(2);
    }

    [Fact]
    public async Task GenerateFromMealPlanAsync_RespectsHouseholdIsolation()
    {
        // Arrange
        var shoppingListId = 1;
        var capturedItems = new List<ShoppingListItem>();

        _shoppingListServiceMock
            .Setup(s => s.CreateShoppingListAsync(1, It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShoppingList
            {
                HouseholdId = 1,
                ShoppingListId = shoppingListId,
                Name = "Test List",
                MealPlanId = 1
            });

        _shoppingListServiceMock
            .Setup(s => s.AddManualItemAsync(It.IsAny<ShoppingListItem>(), It.IsAny<CancellationToken>()))
            .Callback<ShoppingListItem, CancellationToken>((item, _) => capturedItems.Add(item))
            .ReturnsAsync((ShoppingListItem item, CancellationToken _) => item);

        // Act
        var result = await _generator.GenerateFromMealPlanAsync(
            householdId: 1,
            mealPlanId: 1,
            listName: "Week 1 Shopping");

        // Assert
        result.HouseholdId.Should().Be(1);

        // All captured items should be for household 1
        capturedItems.Should().OnlyContain(i => i.HouseholdId == 1);

        // Should NOT contain household 2's milk (5 cups)
        var milkItems = capturedItems.Where(i => i.Name == "milk").ToList();
        milkItems.Should().HaveCount(1);
        milkItems.First().Quantity.Should().Be(1.5m); // Only from household 1 recipes (1 + 0.5 cups)
    }

    [Fact]
    public async Task GenerateFromMealPlanAsync_ConsolidatesAcrossMultipleRecipes()
    {
        // Arrange
        var capturedItems = new List<ShoppingListItem>();

        _shoppingListServiceMock
            .Setup(s => s.CreateShoppingListAsync(1, It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShoppingList
            {
                HouseholdId = 1,
                ShoppingListId = 1,
                Name = "Test List",
                MealPlanId = 1
            });

        _shoppingListServiceMock
            .Setup(s => s.AddManualItemAsync(It.IsAny<ShoppingListItem>(), It.IsAny<CancellationToken>()))
            .Callback<ShoppingListItem, CancellationToken>((item, _) => capturedItems.Add(item))
            .ReturnsAsync((ShoppingListItem item, CancellationToken _) => item);

        // Act
        await _generator.GenerateFromMealPlanAsync(
            householdId: 1,
            mealPlanId: 1,
            listName: "Week 1 Shopping");

        // Assert
        // Verify items were captured from the meal plan
        capturedItems.Should().NotBeEmpty("shopping list items should be generated from meal plan");

        // Milk appears in Pancakes (1 cup) and/or French Toast (0.5 cup)
        var milkItem = capturedItems.FirstOrDefault(i => i.Name == "milk");
        milkItem.Should().NotBeNull("milk should be in the shopping list");
        milkItem!.Quantity.Should().BeGreaterThan(0);

        // Eggs appear in recipes - verify they're included
        var eggsItem = capturedItems.FirstOrDefault(i => i.Name == "eggs");
        eggsItem.Should().NotBeNull("eggs should be in the shopping list");
        eggsItem!.Quantity.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task GenerateFromMealPlanAsync_TracksSourceRecipes()
    {
        // Arrange
        var capturedItems = new List<ShoppingListItem>();

        _shoppingListServiceMock
            .Setup(s => s.CreateShoppingListAsync(1, It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShoppingList
            {
                HouseholdId = 1,
                ShoppingListId = 1,
                Name = "Test List",
                MealPlanId = 1
            });

        _shoppingListServiceMock
            .Setup(s => s.AddManualItemAsync(It.IsAny<ShoppingListItem>(), It.IsAny<CancellationToken>()))
            .Callback<ShoppingListItem, CancellationToken>((item, _) => capturedItems.Add(item))
            .ReturnsAsync((ShoppingListItem item, CancellationToken _) => item);

        // Act
        await _generator.GenerateFromMealPlanAsync(
            householdId: 1,
            mealPlanId: 1,
            listName: "Week 1 Shopping");

        // Assert
        // Flour is only in Pancakes
        var flourItem = capturedItems.FirstOrDefault(i => i.Name == "flour");
        flourItem.Should().NotBeNull();
        flourItem!.SourceRecipes.Should().Contain("Pancakes");
        flourItem.SourceRecipes.Should().NotContain("French Toast");

        // Steak is only in Steak Dinner
        var steakItem = capturedItems.FirstOrDefault(i => i.Name == "steak");
        steakItem.Should().NotBeNull();
        steakItem!.SourceRecipes.Should().Contain("Steak Dinner");
    }

    [Fact]
    public async Task GenerateFromMealPlanAsync_ThrowsForNonExistentMealPlan()
    {
        // Act
        var act = async () => await _generator.GenerateFromMealPlanAsync(
            householdId: 1,
            mealPlanId: 999,
            listName: "Invalid Plan");

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*MealPlan 999 not found*");
    }

    [Fact]
    public async Task GenerateFromMealPlanAsync_FiltersByDateRange()
    {
        // Arrange
        var capturedItems = new List<ShoppingListItem>();

        _shoppingListServiceMock
            .Setup(s => s.CreateShoppingListAsync(1, It.IsAny<string>(), It.IsAny<int?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ShoppingList
            {
                HouseholdId = 1,
                ShoppingListId = 1,
                Name = "Test List",
                MealPlanId = 1
            });

        _shoppingListServiceMock
            .Setup(s => s.AddManualItemAsync(It.IsAny<ShoppingListItem>(), It.IsAny<CancellationToken>()))
            .Callback<ShoppingListItem, CancellationToken>((item, _) => capturedItems.Add(item))
            .ReturnsAsync((ShoppingListItem item, CancellationToken _) => item);

        // Act - Only include today (Pancakes)
        await _generator.GenerateFromMealPlanAsync(
            householdId: 1,
            mealPlanId: 1,
            listName: "Today Only",
            startDate: DateOnly.FromDateTime(DateTime.Today),
            endDate: DateOnly.FromDateTime(DateTime.Today));

        // Assert
        // Should only have Pancake ingredients, not French Toast or Steak
        capturedItems.Should().Contain(i => i.Name == "flour"); // Pancakes
        capturedItems.Should().NotContain(i => i.Name == "bread"); // French Toast
        capturedItems.Should().NotContain(i => i.Name == "steak"); // Steak Dinner
    }

    [Fact]
    public async Task ConsolidateIngredientsAsync_NormalizesIngredientNames()
    {
        // Arrange - Same ingredient with slightly different names should consolidate
        var recipe = new Recipe { HouseholdId = 1, RecipeId = 100, Name = "Test Recipe" };
        var ingredients = new List<RecipeIngredient>
        {
            new()
            {
                HouseholdId = 1,
                RecipeId = 100,
                IngredientId = 1,
                Name = "Fresh Garlic",
                Quantity = 2,
                Unit = "cloves",
                Category = "Produce",
                Recipe = recipe
            },
            new()
            {
                HouseholdId = 1,
                RecipeId = 100,
                IngredientId = 2,
                Name = "garlic", // Same item, different name format
                Quantity = 3,
                Unit = "cloves",
                Category = "Produce",
                Recipe = recipe
            }
        };

        // Act
        var results = await _generator.ConsolidateIngredientsAsync(ingredients, autoConsolidate: true);

        // Assert - Should consolidate because normalized names match
        results.Should().HaveCount(1);
        results.First().Quantity.Should().Be(5m); // 2 + 3 cloves
    }

    // ─── Servings-aware generation (F2) ──────────────────────────────────────

    [Theory]
    // The whole point: an override against a recipe that declares its yield.
    [InlineData(8, 4, 2.0)]
    [InlineData(2, 4, 0.5)]
    [InlineData(4, 4, 1.0)]
    // Nothing to scale against ⇒ exactly 1, i.e. the pre-feature behaviour. Every one of these is a
    // real row in this database today: most entries carry no override, and Recipe.Servings is nullable.
    [InlineData(null, 4, 1.0)]
    [InlineData(8, null, 1.0)]
    [InlineData(null, null, 1.0)]
    // Non-positive on either side is meaningless, not a licence to divide by zero or zero the list out.
    [InlineData(0, 4, 1.0)]
    [InlineData(8, 0, 1.0)]
    [InlineData(-2, 4, 1.0)]
    public void ScaleFactorFor_IsOneWheneverThereIsNothingToScaleAgainst(
        int? entryServings, int? recipeServings, double expected)
    {
        ShoppingListGenerator.ScaleFactorFor(entryServings, recipeServings)
            .Should().Be((decimal)expected);
    }

    [Fact]
    public async Task ConsolidateScaledIngredients_ScalesEachSourceByItsOwnFactor()
    {
        // The case that forces the factor to travel INTO consolidation rather than being applied per
        // recipe beforehand: one recipe planned twice in the same week at different servings. Grouping by
        // ingredient name destroys the entry association, so a factor applied any later is unattributable.
        var recipe = new Recipe { HouseholdId = 1, RecipeId = 200, Name = "Chili", Servings = 4 };
        var ingredient = new RecipeIngredient
        {
            HouseholdId = 1,
            RecipeId = 200,
            IngredientId = 1,
            Name = "beans",
            Quantity = 2m,
            Unit = "cup",
            Category = "Pantry",
            Recipe = recipe
        };

        var scaled = new List<ScaledIngredient>
        {
            new(ingredient, ShoppingListGenerator.ScaleFactorFor(8, recipe.Servings)),   // ×2 → 4 cups
            new(ingredient, ShoppingListGenerator.ScaleFactorFor(2, recipe.Servings)),   // ×0.5 → 1 cup
        };

        var results = await _generator.ConsolidateScaledIngredientsAsync(scaled, autoConsolidate: true);

        results.Should().HaveCount(1);
        results[0].Quantity.Should().Be(5m, "4 cups for the party plus 1 cup for the small night");
        results[0].Unit.Should().Be("cup");
        // The per-source breakdown must show the amounts actually being bought, not the recipe's own.
        results[0].OriginalUnits.Should().Be("4 cup + 1 cup");
    }

    [Fact]
    public async Task ConsolidateScaledIngredients_RoundsToTheStoredPrecision()
    {
        // ShoppingListItem.Quantity is decimal(10,2). A factor of 1/3 is exactly the kind of value that
        // would otherwise be computed at full precision and silently rounded on write, so the number in
        // the result would not be the number in the list.
        var recipe = new Recipe { HouseholdId = 1, RecipeId = 201, Name = "Thirds", Servings = 3 };
        var ingredient = new RecipeIngredient
        {
            HouseholdId = 1,
            RecipeId = 201,
            IngredientId = 1,
            Name = "flour",
            Quantity = 1m,
            Unit = "cup",
            Category = "Pantry",
            Recipe = recipe
        };

        var results = await _generator.ConsolidateScaledIngredientsAsync(
            [new ScaledIngredient(ingredient, ShoppingListGenerator.ScaleFactorFor(1, 3))],
            autoConsolidate: true);

        results.Should().HaveCount(1);
        results[0].Quantity.Should().Be(0.33m);
    }

    [Fact]
    public async Task ConsolidateScaledIngredients_ScalesItemsKeptSeparate()
    {
        // The keep-separate branch is a different code path from the consolidating one, and it was just as
        // capable of ignoring the factor. Volume vs weight is the pair the existing incompatible-units test
        // uses, so it is known to survive FindCommonUnit rather than being an assumption of this test.
        var recipe = new Recipe { HouseholdId = 1, RecipeId = 202, Name = "Mixed", Servings = 2 };
        RecipeIngredient Ing(int id, string unit) => new()
        {
            HouseholdId = 1,
            RecipeId = 202,
            IngredientId = id,
            Name = "olive oil",
            Quantity = 3m,
            Unit = unit,
            Category = "Pantry",
            Recipe = recipe
        };

        var results = await _generator.ConsolidateScaledIngredientsAsync(
            [
                new ScaledIngredient(Ing(1, "cups"), 2m),
                new ScaledIngredient(Ing(2, "g"), 2m),
            ],
            autoConsolidate: true);

        results.Should().HaveCount(2, "volume and weight have no common unit");
        results.Should().OnlyContain(r => r.Quantity == 6m, "both sides of the split must honour the factor");
    }

    [Fact]
    public async Task GenerateFromMealPlan_ScalesOnlyTheEntriesThatAskForIt()
    {
        // End-to-end through the real entity graph: two entries in one plan, one overridden and one not.
        // The un-overridden entry is the regression guard for the promise this feature makes — a recipe
        // deliberately batch-sized for leftovers must not be silently rescaled.
        var scaled = new Recipe { HouseholdId = 1, RecipeId = 300, Name = "Curry", Servings = 4 };
        var untouched = new Recipe { HouseholdId = 1, RecipeId = 301, Name = "Soup", Servings = 4 };
        _context.Recipes.AddRange(scaled, untouched);
        _context.RecipeIngredients.AddRange(
            new RecipeIngredient
            {
                HouseholdId = 1, RecipeId = 300, IngredientId = 1,
                Name = "rice", Quantity = 2m, Unit = "cup", Category = "Pantry"
            },
            new RecipeIngredient
            {
                HouseholdId = 1, RecipeId = 301, IngredientId = 1,
                Name = "stock", Quantity = 2m, Unit = "cup", Category = "Pantry"
            });

        var plan = new MealPlan { HouseholdId = 1, MealPlanId = 900, WeekStartDate = new DateOnly(2026, 6, 1) };
        _context.MealPlans.Add(plan);
        _context.MealPlanEntries.AddRange(
            new MealPlanEntry
            {
                HouseholdId = 1, MealPlanId = 900, EntryId = 1,
                Date = new DateOnly(2026, 6, 1), MealType = MealType.Dinner,
                RecipeId = 300, Servings = 12   // ×3 against a recipe that yields 4
            },
            new MealPlanEntry
            {
                HouseholdId = 1, MealPlanId = 900, EntryId = 2,
                Date = new DateOnly(2026, 6, 2), MealType = MealType.Dinner,
                RecipeId = 301, Servings = null // as written
            });
        await _context.SaveChangesAsync();

        var created = new ShoppingList { HouseholdId = 1, ShoppingListId = 77, Name = "Week" };
        _shoppingListServiceMock
            .Setup(s => s.CreateShoppingListAsync(1, "Week", 900, It.IsAny<CancellationToken>()))
            .ReturnsAsync(created);
        var added = new List<ShoppingListItem>();
        _shoppingListServiceMock
            .Setup(s => s.AddManualItemAsync(It.IsAny<ShoppingListItem>(), It.IsAny<CancellationToken>()))
            .Callback<ShoppingListItem, CancellationToken>((i, _) => added.Add(i))
            .ReturnsAsync((ShoppingListItem i, CancellationToken _) => i);

        await _generator.GenerateFromMealPlanAsync(1, 900, "Week");

        added.Single(i => i.Name == "rice").Quantity.Should().Be(6m, "12 servings of a recipe that yields 4");
        added.Single(i => i.Name == "stock").Quantity.Should().Be(2m, "no override — exactly as before this feature");
    }
}
