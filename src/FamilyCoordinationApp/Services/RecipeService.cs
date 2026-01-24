using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public interface IRecipeService
{
    Task<List<Recipe>> GetRecipesAsync(int householdId, string? searchTerm = null, CancellationToken cancellationToken = default);
    Task<Recipe?> GetRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<Recipe> CreateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);
    Task<Recipe> UpdateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);
    Task DeleteRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<int> GetNextRecipeIdAsync(int householdId, CancellationToken cancellationToken = default);
    Task<List<string>> GetIngredientSuggestionsAsync(int householdId, string prefix, CancellationToken cancellationToken = default);
}

public class RecipeService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<RecipeService> logger) : IRecipeService
{

    public async Task<List<Recipe>> GetRecipesAsync(int householdId, string? searchTerm = null, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = context.Recipes
            .Where(r => r.HouseholdId == householdId)
            .Include(r => r.Ingredients.OrderBy(i => i.SortOrder))
            .Include(r => r.CreatedBy)
            .AsQueryable();

        if (!string.IsNullOrWhiteSpace(searchTerm))
        {
            var term = searchTerm.Trim().ToLower();
            query = query.Where(r => r.Name.ToLower().Contains(term));
        }

        return await query
            .OrderBy(r => r.Name)
            .ToListAsync(cancellationToken);
    }

    public async Task<Recipe?> GetRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.Recipes
            .Where(r => r.HouseholdId == householdId && r.RecipeId == recipeId)
            .Include(r => r.Ingredients.OrderBy(i => i.SortOrder))
            .Include(r => r.CreatedBy)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<Recipe> CreateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Get next recipe ID for this household
        recipe.RecipeId = await GetNextRecipeIdInternalAsync(context, recipe.HouseholdId, cancellationToken);
        recipe.CreatedAt = DateTime.UtcNow;

        // Set ingredient IDs and household IDs
        var ingredientId = 1;
        foreach (var ingredient in recipe.Ingredients)
        {
            ingredient.HouseholdId = recipe.HouseholdId;
            ingredient.RecipeId = recipe.RecipeId;
            ingredient.IngredientId = ingredientId++;
        }

        context.Recipes.Add(recipe);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Created recipe {RecipeId} for household {HouseholdId}", recipe.RecipeId, recipe.HouseholdId);

        return recipe;
    }

    public async Task<Recipe> UpdateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Load existing recipe with ingredients
        var existing = await context.Recipes
            .Include(r => r.Ingredients)
            .FirstOrDefaultAsync(r => r.HouseholdId == recipe.HouseholdId && r.RecipeId == recipe.RecipeId, cancellationToken);

        if (existing == null)
        {
            throw new InvalidOperationException($"Recipe {recipe.RecipeId} not found in household {recipe.HouseholdId}");
        }

        // Update scalar properties
        existing.Name = recipe.Name;
        existing.Description = recipe.Description;
        existing.Instructions = recipe.Instructions;
        existing.ImagePath = recipe.ImagePath;
        existing.SourceUrl = recipe.SourceUrl;
        existing.Servings = recipe.Servings;
        existing.PrepTimeMinutes = recipe.PrepTimeMinutes;
        existing.CookTimeMinutes = recipe.CookTimeMinutes;
        existing.UpdatedAt = DateTime.UtcNow;
        existing.UpdatedByUserId = recipe.UpdatedByUserId;

        // Replace ingredients (simpler than tracking changes)
        context.RecipeIngredients.RemoveRange(existing.Ingredients);

        var ingredientId = 1;
        foreach (var ingredient in recipe.Ingredients)
        {
            var newIngredient = new RecipeIngredient
            {
                HouseholdId = recipe.HouseholdId,
                RecipeId = recipe.RecipeId,
                IngredientId = ingredientId++,
                Name = ingredient.Name,
                Quantity = ingredient.Quantity,
                Unit = ingredient.Unit,
                Category = ingredient.Category,
                Notes = ingredient.Notes,
                GroupName = ingredient.GroupName,
                SortOrder = ingredient.SortOrder
            };
            context.RecipeIngredients.Add(newIngredient);
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated recipe {RecipeId} for household {HouseholdId}", recipe.RecipeId, recipe.HouseholdId);

        return existing;
    }

    public async Task DeleteRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var recipe = await context.Recipes
            .FirstOrDefaultAsync(r => r.HouseholdId == householdId && r.RecipeId == recipeId, cancellationToken);

        if (recipe == null)
        {
            throw new InvalidOperationException($"Recipe {recipeId} not found in household {householdId}");
        }

        // Soft delete
        recipe.IsDeleted = true;
        recipe.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Soft deleted recipe {RecipeId} for household {HouseholdId}", recipeId, householdId);
    }

    public async Task<int> GetNextRecipeIdAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);
        return await GetNextRecipeIdInternalAsync(context, householdId, cancellationToken);
    }

    private static async Task<int> GetNextRecipeIdInternalAsync(ApplicationDbContext context, int householdId, CancellationToken cancellationToken)
    {
        var maxId = await context.Recipes
            .IgnoreQueryFilters() // Include soft-deleted
            .Where(r => r.HouseholdId == householdId)
            .MaxAsync(r => (int?)r.RecipeId, cancellationToken) ?? 0;

        return maxId + 1;
    }

    public async Task<List<string>> GetIngredientSuggestionsAsync(int householdId, string prefix, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(prefix) || prefix.Length < 2)
            return new List<string>();

        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.RecipeIngredients
            .Where(i => i.HouseholdId == householdId && i.Name.StartsWith(prefix))
            .Select(i => i.Name)
            .Distinct()
            .Take(20)
            .ToListAsync(cancellationToken);
    }
}
