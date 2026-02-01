using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services.Interfaces;

public interface IShoppingListService
{
    Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken cancellationToken = default);
    Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken cancellationToken = default);
    Task<List<ShoppingList>> GetArchivedShoppingListsAsync(int householdId, bool? favoritesOnly = null, CancellationToken cancellationToken = default);
    Task RestoreShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task DeleteShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<ShoppingList> RenameShoppingListAsync(int householdId, int shoppingListId, string newName, CancellationToken cancellationToken = default);
    Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default);
    Task UpdateItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default);
    Task<(bool Success, bool WasConflict, string? ConflictMessage)> UpdateItemWithConcurrencyAsync(ShoppingListItem item, CancellationToken cancellationToken = default);
    Task UpdateItemSortOrdersAsync(int householdId, int shoppingListId, List<(int ItemId, int SortOrder, string? Category)> updates, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default);
    Task<ShoppingListItem> ToggleItemCheckedAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default);
    Task<ShoppingList> ToggleFavoriteAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, int limit = 10, CancellationToken cancellationToken = default);
    Task ArchiveShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<int> ClearCheckedItemsAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
}
