using Bogus;
using FamilyCoordinationApp.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace FamilyCoordinationApp.Data;

public static class SeedData
{
    public static async Task SeedDevelopmentDataAsync(IDbContextFactory<ApplicationDbContext> dbFactory)
    {
        using var context = dbFactory.CreateDbContext();

        // Only seed if no recipes exist
        if (await context.Recipes.AnyAsync())
            return;

        var household = await context.Households.FirstOrDefaultAsync();
        if (household == null)
            return;

        var user = await context.Users.FirstOrDefaultAsync(u => u.HouseholdId == household.Id);
        if (user == null)
            return;

        // Seed default categories
        await SeedDefaultCategoriesAsync(dbFactory, household.Id);

        // Real recipe names for realistic data
        var recipeNames = new[]
        {
            "Spaghetti Bolognese",
            "Chicken Stir Fry",
            "Beef Tacos",
            "Grilled Salmon",
            "Vegetable Curry",
            "Caesar Salad",
            "Mushroom Risotto",
            "BBQ Pulled Pork",
            "Thai Green Curry",
            "Lasagna",
            "Chicken Parmesan",
            "Shrimp Scampi",
            "Beef Stroganoff",
            "Fish and Chips",
            "Pad Thai"
        };

        var categories = new[] { "Meat", "Produce", "Dairy", "Pantry", "Spices" };
        var units = new[] { "lb", "oz", "cup", "tbsp", "tsp", "piece", "clove", "can" };

        var ingredientFaker = new Faker<RecipeIngredient>()
            .RuleFor(i => i.Name, f => f.Commerce.ProductName())
            .RuleFor(i => i.Quantity, f => f.Random.Decimal(0.25m, 4m))
            .RuleFor(i => i.Unit, f => f.PickRandom(units))
            .RuleFor(i => i.Category, f => f.PickRandom(categories))
            .RuleFor(i => i.SortOrder, (f, i) => f.IndexFaker);

        var recipeId = 1;
        foreach (var name in recipeNames)
        {
            var recipe = new Recipe
            {
                HouseholdId = household.Id,
                RecipeId = recipeId++,
                Name = name,
                Description = $"A delicious {name.ToLower()} recipe",
                Instructions = $"1. Prepare ingredients\n2. Cook {name.ToLower()}\n3. Serve hot",
                Servings = new Random().Next(2, 8),
                PrepTimeMinutes = new Random().Next(10, 30),
                CookTimeMinutes = new Random().Next(15, 60),
                CreatedByUserId = user.Id,
                CreatedAt = DateTime.UtcNow.AddDays(-new Random().Next(1, 30))
            };

            // Add 4-8 ingredients per recipe
            var ingredientCount = new Random().Next(4, 9);
            for (int i = 0; i < ingredientCount; i++)
            {
                var ingredient = ingredientFaker.Generate();
                ingredient.HouseholdId = household.Id;
                ingredient.RecipeId = recipe.RecipeId;
                ingredient.IngredientId = i + 1;
                recipe.Ingredients.Add(ingredient);
            }

            context.Recipes.Add(recipe);
        }

        await context.SaveChangesAsync();
    }

    public static async Task SeedDefaultCategoriesAsync(IDbContextFactory<ApplicationDbContext> dbFactory, int householdId)
    {
        await using var context = dbFactory.CreateDbContext();

        // Check if categories already exist for this household
        var existingCount = await context.Categories
            .IgnoreQueryFilters()  // Include soft-deleted
            .CountAsync(c => c.HouseholdId == householdId);

        if (existingCount > 0) return;

        var defaultCategories = new[]
        {
            new Category { HouseholdId = householdId, CategoryId = 1, Name = "Meat", IconEmoji = "meat_on_bone", Color = "#b71c1c", SortOrder = 1, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 2, Name = "Produce", IconEmoji = "leafy_green", Color = "#2e7d32", SortOrder = 2, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 3, Name = "Dairy", IconEmoji = "cheese_wedge", Color = "#ffc107", SortOrder = 3, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 4, Name = "Pantry", IconEmoji = "canned_food", Color = "#795548", SortOrder = 4, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 5, Name = "Spices", IconEmoji = "hot_pepper", Color = "#ff5722", SortOrder = 5, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 6, Name = "Frozen", IconEmoji = "snowflake", Color = "#03a9f4", SortOrder = 6, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 7, Name = "Bakery", IconEmoji = "bread", Color = "#8d6e63", SortOrder = 7, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 8, Name = "Beverages", IconEmoji = "cup_with_straw", Color = "#9c27b0", SortOrder = 8, IsDefault = true },
            new Category { HouseholdId = householdId, CategoryId = 9, Name = "Other", IconEmoji = "package", Color = "#607d8b", SortOrder = 9, IsDefault = true }
        };

        context.Categories.AddRange(defaultCategories);
        await context.SaveChangesAsync();
    }
}
