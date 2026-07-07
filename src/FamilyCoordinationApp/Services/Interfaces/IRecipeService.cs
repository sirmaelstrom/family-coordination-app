using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IRecipeService
{
    Task<List<Recipe>> GetRecipesAsync(int householdId, string? searchTerm = null, CancellationToken cancellationToken = default);
    Task<Recipe?> GetRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<Recipe> CreateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);
    /// <summary>
    /// Full-form update (wholesale ingredient replace). When <paramref name="expectedVersion"/> is supplied it
    /// is enforced as the xmin concurrency token — a stale value throws <see cref="RecipeConflictException"/>
    /// (→ 409). <c>null</c> skips the check (legacy last-write-wins callers).
    /// </summary>
    /// <exception cref="RecipeConflictException">The client <paramref name="expectedVersion"/> is stale.</exception>
    Task<Recipe> UpdateRecipeAsync(Recipe recipe, uint? expectedVersion = null, CancellationToken cancellationToken = default);
    Task DeleteRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<int> GetNextRecipeIdAsync(int householdId, CancellationToken cancellationToken = default);
    Task<List<string>> GetIngredientSuggestionsAsync(int householdId, string prefix, CancellationToken cancellationToken = default);

    // Favorites
    Task<bool> IsFavoriteAsync(int userId, int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<HashSet<int>> GetFavoriteRecipeIdsAsync(int userId, int householdId, CancellationToken cancellationToken = default);
    Task ToggleFavoriteAsync(int userId, int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<List<Recipe>> GetFavoriteRecipesAsync(int userId, int householdId, CancellationToken cancellationToken = default);

    // Connected household recipes
    Task<List<Recipe>> GetRecipesFromConnectedHouseholdAsync(int viewingHouseholdId, int connectedHouseholdId, string? searchTerm = null, CancellationToken cancellationToken = default);
    Task<Recipe> CopyRecipeFromConnectedHouseholdAsync(int sourceHouseholdId, int sourceRecipeId, int targetHouseholdId, int userId, CancellationToken cancellationToken = default);
}
