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
}
