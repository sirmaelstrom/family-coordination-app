using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IRecipeService
{
    Task<List<Recipe>> GetRecipesAsync(int householdId, string? searchTerm = null, CancellationToken cancellationToken = default);
    Task<Recipe?> GetRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<Recipe> CreateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);
    Task<Recipe> UpdateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);
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
