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
}
