using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IShoppingListService
{
    Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken cancellationToken = default);
    Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken cancellationToken = default);
    Task<ShoppingList> RenameShoppingListAsync(int householdId, int shoppingListId, string newName, CancellationToken cancellationToken = default);
    Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default);
    /// <summary>
    /// Apply <paramref name="item"/>'s editable fields, checked against the client's <paramref name="version"/>: a
    /// stale or missing token throws <see cref="ConcurrencyConflictException"/> (→ 409); a gone item throws
    /// <see cref="NotFoundException"/>. On success the saved values and the new Version are copied back onto
    /// <paramref name="item"/>.
    /// </summary>
    Task UpdateItemAsync(ShoppingListItem item, uint version, CancellationToken cancellationToken = default);
    /// <summary>Reorder; writes only the rows whose order or category changed, and returns every listed item's
    /// version after the save.</summary>
    Task<IReadOnlyList<(int ItemId, uint VersionBefore, uint Version)>> UpdateItemSortOrdersAsync(int householdId, int shoppingListId, List<(int ItemId, int SortOrder, string? Category)> updates, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default);
    Task<ShoppingList> ToggleFavoriteAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task ArchiveShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<int> ClearCheckedItemsAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);

    // The past-lists trio, wired at GET /archived, POST /{id}/actions/restore and DELETE /{id}
    // (delete is server-enforced archived-only). Suggestions reads the household's whole item
    // history, archived lists included.
    Task<List<ShoppingList>> GetArchivedShoppingListsAsync(int householdId, bool? favoritesOnly = null, CancellationToken cancellationToken = default);
    Task RestoreShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task DeleteShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, int limit = 10, CancellationToken cancellationToken = default);
}
