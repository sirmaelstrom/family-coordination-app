using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IRecipeService
{
    Task<List<Recipe>> GetRecipesAsync(int householdId, string? searchTerm = null, CancellationToken cancellationToken = default);
    Task<Recipe?> GetRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<Recipe> CreateRecipeAsync(Recipe recipe, CancellationToken cancellationToken = default);
    /// <summary>
    /// Full-form update (wholesale ingredient replace). <paramref name="expectedVersion"/> is enforced as the xmin
    /// concurrency token: a stale value, or <see cref="Data.ConcurrencySave.MissingToken"/> for a request that sent
    /// none, throws <see cref="ConcurrencyConflictException"/> (→ 409). There is no last-write-wins path (quest 8cf3c806).
    /// </summary>
    /// <exception cref="ConcurrencyConflictException">The client <paramref name="expectedVersion"/> is stale or missing.</exception>
    Task<Recipe> UpdateRecipeAsync(Recipe recipe, uint expectedVersion, CancellationToken cancellationToken = default);
    /// <summary>
    /// Point one recipe at an image, checked against <paramref name="expectedVersion"/> like
    /// <see cref="UpdateRecipeAsync"/>. Nothing else on the recipe changes. The caller validates the path
    /// (<c>ImagePathPolicy</c>); the previous file is not deleted, as the app's own editor leaves it too.
    /// </summary>
    /// <exception cref="NotFoundException">No such recipe in the caller's household.</exception>
    /// <exception cref="ConcurrencyConflictException"><paramref name="expectedVersion"/> is stale.</exception>
    Task SetImagePathAsync(int recipeId, string imagePath, uint expectedVersion, CancellationToken cancellationToken = default);
    Task DeleteRecipeAsync(int householdId, int recipeId, CancellationToken cancellationToken = default);
    /// <summary>
    /// The caller's household's soft-deleted recipes, most recently deleted first. Read past the
    /// <c>SoftDelete</c> filter only, so the <c>Tenant</c> filter still scopes it. No ingredients or author loaded.
    /// </summary>
    Task<List<Recipe>> GetDeletedRecipesAsync(CancellationToken cancellationToken = default);
    /// <summary>
    /// Bring a soft-deleted recipe back. Its ingredients, favorites and planned meals were kept, so they return
    /// with it. An already-active recipe is left as it is (idempotent).
    /// </summary>
    /// <exception cref="NotFoundException">No such recipe, deleted or not, in the caller's household.</exception>
    Task RestoreRecipeAsync(int recipeId, CancellationToken cancellationToken = default);
    Task<List<string>> GetIngredientSuggestionsAsync(int householdId, string prefix, CancellationToken cancellationToken = default);

    // Favorites
    Task<bool> IsFavoriteAsync(int userId, int householdId, int recipeId, CancellationToken cancellationToken = default);
    Task<HashSet<int>> GetFavoriteRecipeIdsAsync(int userId, int householdId, CancellationToken cancellationToken = default);
    Task ToggleFavoriteAsync(int userId, int householdId, int recipeId, CancellationToken cancellationToken = default);

    // Connected household recipes
    Task<List<Recipe>> GetRecipesFromConnectedHouseholdAsync(int viewingHouseholdId, int connectedHouseholdId, string? searchTerm = null, CancellationToken cancellationToken = default);
    /// <summary>
    /// One recipe of a CONNECTED household, read past the Tenant filter (fca-household-scope D6). The caller must
    /// have checked <c>AreHouseholdsConnectedAsync</c> first; <see cref="GetRecipeAsync"/> stays tenant-filtered.
    /// </summary>
    Task<Recipe?> GetConnectedRecipeAsync(int connectedHouseholdId, int recipeId, CancellationToken cancellationToken = default);
    Task<Recipe> CopyRecipeFromConnectedHouseholdAsync(int sourceHouseholdId, int sourceRecipeId, int targetHouseholdId, int userId, CancellationToken cancellationToken = default);
}
