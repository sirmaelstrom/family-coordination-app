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

        // Sample recipes with realistic ingredients
        var sampleRecipes = GetSampleRecipes();
        var recipeId = 1;

        foreach (var (name, description, servings, prepTime, cookTime, ingredients) in sampleRecipes)
        {
            var recipe = new Recipe
            {
                HouseholdId = household.Id,
                RecipeId = recipeId++,
                Name = name,
                Description = description,
                Instructions = $"1. Prepare all ingredients\n2. Follow standard cooking method for {name.ToLower()}\n3. Serve and enjoy",
                Servings = servings,
                PrepTimeMinutes = prepTime,
                CookTimeMinutes = cookTime,
                CreatedByUserId = user.Id,
                CreatedAt = DateTime.UtcNow.AddDays(-recipeId)
            };

            var ingredientId = 1;
            foreach (var (ingName, qty, unit, category) in ingredients)
            {
                recipe.Ingredients.Add(new RecipeIngredient
                {
                    HouseholdId = household.Id,
                    RecipeId = recipe.RecipeId,
                    IngredientId = ingredientId,
                    Name = ingName,
                    Quantity = qty,
                    Unit = unit,
                    Category = category,
                    SortOrder = ingredientId++
                });
            }

            context.Recipes.Add(recipe);
        }

        await context.SaveChangesAsync();
    }

    private static List<(string Name, string Description, int Servings, int PrepTime, int CookTime, 
        List<(string Name, decimal Qty, string Unit, string Category)> Ingredients)> GetSampleRecipes()
    {
        return new()
        {
            ("Spaghetti Bolognese", "Classic Italian meat sauce over pasta", 4, 15, 45, new()
            {
                ("Ground Beef", 1m, "lb", "Meat"),
                ("Spaghetti", 1m, "lb", "Pantry"),
                ("Crushed Tomatoes", 28m, "oz", "Pantry"),
                ("Onion", 1m, "piece", "Produce"),
                ("Garlic", 3m, "clove", "Produce"),
                ("Olive Oil", 2m, "tbsp", "Pantry")
            }),
            ("Chicken Stir Fry", "Quick and healthy Asian-inspired dish", 4, 20, 15, new()
            {
                ("Chicken Breast", 1.5m, "lb", "Meat"),
                ("Bell Peppers", 2m, "piece", "Produce"),
                ("Broccoli", 2m, "cup", "Produce"),
                ("Soy Sauce", 3m, "tbsp", "Pantry"),
                ("Sesame Oil", 1m, "tbsp", "Pantry"),
                ("Ginger", 1m, "tbsp", "Produce")
            }),
            ("Beef Tacos", "Seasoned ground beef in crispy shells", 6, 10, 20, new()
            {
                ("Ground Beef", 1m, "lb", "Meat"),
                ("Taco Shells", 12m, "piece", "Pantry"),
                ("Cheddar Cheese", 1m, "cup", "Dairy"),
                ("Lettuce", 2m, "cup", "Produce"),
                ("Tomato", 2m, "piece", "Produce"),
                ("Taco Seasoning", 1m, "packet", "Spices")
            }),
            ("Grilled Salmon", "Simple herb-crusted salmon fillets", 4, 10, 15, new()
            {
                ("Salmon Fillets", 2m, "lb", "Meat"),
                ("Lemon", 1m, "piece", "Produce"),
                ("Dill", 2m, "tbsp", "Spices"),
                ("Olive Oil", 2m, "tbsp", "Pantry"),
                ("Garlic", 2m, "clove", "Produce")
            }),
            ("Caesar Salad", "Crisp romaine with classic dressing", 4, 15, 0, new()
            {
                ("Romaine Lettuce", 2m, "head", "Produce"),
                ("Parmesan Cheese", 0.5m, "cup", "Dairy"),
                ("Croutons", 1m, "cup", "Pantry"),
                ("Caesar Dressing", 0.5m, "cup", "Dairy"),
                ("Lemon", 1m, "piece", "Produce")
            })
        };
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
