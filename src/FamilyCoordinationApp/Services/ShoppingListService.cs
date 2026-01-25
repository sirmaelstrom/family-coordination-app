using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public interface IShoppingListService
{
    Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken cancellationToken = default);
    Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken cancellationToken = default);
    Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default);
    Task UpdateItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default);
    Task<(bool Success, bool WasConflict, string? ConflictMessage)> UpdateItemWithConcurrencyAsync(ShoppingListItem item, CancellationToken cancellationToken = default);
    Task UpdateItemSortOrdersAsync(int householdId, int shoppingListId, List<(int ItemId, int SortOrder, string? Category)> updates, CancellationToken cancellationToken = default);
    Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default);
    Task<ShoppingListItem> ToggleItemCheckedAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default);
    Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, int limit = 10, CancellationToken cancellationToken = default);
    Task ArchiveShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<int> ClearCheckedItemsAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
}

public class ShoppingListService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ShoppingListService> logger) : IShoppingListService
{

    public async Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken cancellationToken = default)
    {
        return await IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                var maxId = await context.ShoppingLists
                    .Where(sl => sl.HouseholdId == householdId)
                    .MaxAsync(sl => (int?)sl.ShoppingListId, cancellationToken) ?? 0;

                var shoppingList = new ShoppingList
                {
                    HouseholdId = householdId,
                    ShoppingListId = maxId + 1,
                    Name = name,
                    MealPlanId = mealPlanId,
                    CreatedAt = DateTime.UtcNow,
                    IsArchived = false
                };

                context.ShoppingLists.Add(shoppingList);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Created ShoppingList {ShoppingListId} for household {HouseholdId}",
                    shoppingList.ShoppingListId, householdId);

                return shoppingList;
            },
            logger,
            "ShoppingList");
    }

    public async Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.ShoppingLists
            .Where(sl => sl.HouseholdId == householdId && sl.ShoppingListId == shoppingListId)
            .Include(sl => sl.Items)
                .ThenInclude(i => i.AddedBy)
            .Include(sl => sl.MealPlan)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.ShoppingLists
            .Where(sl => sl.HouseholdId == householdId && !sl.IsArchived)
            .OrderByDescending(sl => sl.CreatedAt)
            .Include(sl => sl.Items)
                .ThenInclude(i => i.AddedBy)
            .ToListAsync(cancellationToken);
    }

    public async Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default)
    {
        return await IdGenerationHelper.ExecuteWithRetryAsync(
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                var maxItemId = await context.ShoppingListItems
                    .Where(i => i.HouseholdId == item.HouseholdId && i.ShoppingListId == item.ShoppingListId)
                    .MaxAsync(i => (int?)i.ItemId, cancellationToken) ?? 0;

                item.ItemId = maxItemId + 1;
                item.AddedAt = DateTime.UtcNow;
                item.UpdatedAt = DateTime.UtcNow;
                item.IsChecked = false;

                context.ShoppingListItems.Add(item);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Added manual item {ItemId} to ShoppingList {ShoppingListId} for household {HouseholdId}",
                    item.ItemId, item.ShoppingListId, item.HouseholdId);

                return item;
            },
            logger,
            "ShoppingListItem");
    }

    public async Task UpdateItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.HouseholdId == item.HouseholdId &&
                i.ShoppingListId == item.ShoppingListId &&
                i.ItemId == item.ItemId, cancellationToken);

        if (existing == null)
        {
            throw new InvalidOperationException($"Item {item.ItemId} not found in ShoppingList {item.ShoppingListId} for household {item.HouseholdId}");
        }

        existing.Name = item.Name;
        existing.Quantity = item.Quantity;
        existing.Unit = item.Unit;
        existing.Category = item.Category;
        existing.IsChecked = item.IsChecked;
        existing.CheckedAt = item.CheckedAt;
        existing.UpdatedByUserId = item.UpdatedByUserId;
        existing.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated item {ItemId} in ShoppingList {ShoppingListId} for household {HouseholdId}",
            item.ItemId, item.ShoppingListId, item.HouseholdId);
    }

    /// <summary>
    /// Updates a shopping list item with optimistic concurrency handling.
    /// Uses "checked wins" strategy: if either user checked the item, it stays checked.
    /// </summary>
    /// <returns>True if update succeeded, false if item was deleted by another user</returns>
    public async Task<(bool Success, bool WasConflict, string? ConflictMessage)> UpdateItemWithConcurrencyAsync(ShoppingListItem item, CancellationToken cancellationToken = default)
    {
        const int maxRetries = 3;
        var retries = 0;
        var wasConflict = false;
        string? conflictMessage = null;

        while (retries < maxRetries)
        {
            try
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                // Fetch fresh entity from this context to avoid disposed context error
                var existing = await context.ShoppingListItems
                    .FirstOrDefaultAsync(i =>
                        i.HouseholdId == item.HouseholdId &&
                        i.ShoppingListId == item.ShoppingListId &&
                        i.ItemId == item.ItemId, cancellationToken);

                if (existing == null)
                {
                    logger.LogWarning("Item {ItemId} not found during update", item.ItemId);
                    return (false, false, "Item not found");
                }

                // Apply changes from the passed-in item
                existing.IsChecked = item.IsChecked;
                existing.CheckedAt = item.CheckedAt;
                existing.Name = item.Name;
                existing.Quantity = item.Quantity;
                existing.Unit = item.Unit;
                existing.Category = item.Category;
                existing.UpdatedByUserId = item.UpdatedByUserId;
                existing.UpdatedAt = DateTime.UtcNow;

                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Updated item {ItemId} in ShoppingList {ShoppingListId} for household {HouseholdId} (conflict: {WasConflict})",
                    item.ItemId, item.ShoppingListId, item.HouseholdId, wasConflict);

                return (true, wasConflict, conflictMessage);
            }
            catch (DbUpdateConcurrencyException ex)
            {
                retries++;
                wasConflict = true;

                foreach (var entry in ex.Entries)
                {
                    if (entry.Entity is ShoppingListItem)
                    {
                        var databaseValues = await entry.GetDatabaseValuesAsync(cancellationToken);

                        if (databaseValues == null)
                        {
                            // Item was deleted by another user
                            logger.LogWarning("Item {ItemId} was deleted by another user during update", item.ItemId);
                            return (false, true, "This item was deleted by another family member");
                        }

                        var proposedChecked = (bool)(entry.CurrentValues[nameof(ShoppingListItem.IsChecked)] ?? false);
                        var databaseChecked = (bool)(databaseValues[nameof(ShoppingListItem.IsChecked)] ?? false);

                        // "Checked wins" - if either user checked it, keep it checked
                        if (proposedChecked || databaseChecked)
                        {
                            entry.CurrentValues[nameof(ShoppingListItem.IsChecked)] = true;
                            entry.CurrentValues[nameof(ShoppingListItem.CheckedAt)] =
                                entry.CurrentValues[nameof(ShoppingListItem.CheckedAt)] ??
                                databaseValues[nameof(ShoppingListItem.CheckedAt)] ??
                                DateTime.UtcNow;
                        }

                        // For quantity/name changes, check if there's a true conflict
                        var proposedName = (string?)entry.CurrentValues[nameof(ShoppingListItem.Name)] ?? string.Empty;
                        var databaseName = (string?)databaseValues[nameof(ShoppingListItem.Name)] ?? string.Empty;
                        var proposedQty = entry.CurrentValues[nameof(ShoppingListItem.Quantity)] as decimal?;
                        var databaseQty = databaseValues[nameof(ShoppingListItem.Quantity)] as decimal?;

                        if (proposedName != databaseName || proposedQty != databaseQty)
                        {
                            // For now, use last-write-wins for non-checkbox fields
                            // but record that there was a conflict
                            conflictMessage = "Another family member also edited this item";

                            logger.LogInformation("Concurrent edit detected on item {ItemId}: Name({ProposedName} vs {DatabaseName}), Qty({ProposedQty} vs {DatabaseQty})",
                                item.ItemId, proposedName, databaseName, proposedQty, databaseQty);
                        }

                        // Refresh original values to allow retry
                        entry.OriginalValues.SetValues(databaseValues);
                    }
                }

                if (retries >= maxRetries)
                {
                    logger.LogError("Failed to update item {ItemId} after {MaxRetries} retries due to concurrency conflicts",
                        item.ItemId, maxRetries);
                    return (false, true, "Could not save changes after multiple attempts");
                }
            }
        }

        return (true, wasConflict, conflictMessage);
    }

    public async Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var item = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.HouseholdId == householdId &&
                i.ShoppingListId == shoppingListId &&
                i.ItemId == itemId, cancellationToken);

        if (item == null)
        {
            throw new InvalidOperationException($"Item {itemId} not found in ShoppingList {shoppingListId} for household {householdId}");
        }

        context.ShoppingListItems.Remove(item);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted item {ItemId} from ShoppingList {ShoppingListId} for household {HouseholdId}",
            itemId, shoppingListId, householdId);
    }

    public async Task<ShoppingListItem> ToggleItemCheckedAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var item = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.HouseholdId == householdId &&
                i.ShoppingListId == shoppingListId &&
                i.ItemId == itemId, cancellationToken);

        if (item == null)
        {
            throw new InvalidOperationException($"Item {itemId} not found in ShoppingList {shoppingListId} for household {householdId}");
        }

        item.IsChecked = !item.IsChecked;
        item.CheckedAt = item.IsChecked ? DateTime.UtcNow : null;
        item.UpdatedAt = DateTime.UtcNow;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Toggled item {ItemId} checked state to {IsChecked} in ShoppingList {ShoppingListId} for household {HouseholdId}",
            itemId, item.IsChecked, shoppingListId, householdId);

        return item;
    }

    public async Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, int limit = 10, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var lower = prefix.ToLowerInvariant();

        // Search ALL shopping list items (including archived lists) for autocomplete
        // Prioritize StartsWith matches, fallback to Contains
        var startsWithMatches = await context.ShoppingListItems
            .Where(i => i.HouseholdId == householdId && i.Name.ToLower().StartsWith(lower))
            .GroupBy(i => i.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(limit)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);

        if (startsWithMatches.Count >= limit)
        {
            return startsWithMatches;
        }

        // Fallback to Contains for additional matches
        var containsMatches = await context.ShoppingListItems
            .Where(i => i.HouseholdId == householdId
                     && i.Name.ToLower().Contains(lower)
                     && !i.Name.ToLower().StartsWith(lower))
            .GroupBy(i => i.Name)
            .Select(g => new { Name = g.Key, Count = g.Count() })
            .OrderByDescending(x => x.Count)
            .Take(limit - startsWithMatches.Count)
            .Select(x => x.Name)
            .ToListAsync(cancellationToken);

        return startsWithMatches.Concat(containsMatches).ToList();
    }

    public async Task ArchiveShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var shoppingList = await context.ShoppingLists
            .FirstOrDefaultAsync(sl =>
                sl.HouseholdId == householdId &&
                sl.ShoppingListId == shoppingListId, cancellationToken);

        if (shoppingList == null)
        {
            throw new InvalidOperationException($"ShoppingList {shoppingListId} not found for household {householdId}");
        }

        shoppingList.IsArchived = true;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Archived ShoppingList {ShoppingListId} for household {HouseholdId}",
            shoppingListId, householdId);
    }

    public async Task<int> ClearCheckedItemsAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var checkedItems = await context.ShoppingListItems
            .Where(i =>
                i.HouseholdId == householdId &&
                i.ShoppingListId == shoppingListId &&
                i.IsChecked)
            .ToListAsync(cancellationToken);

        var count = checkedItems.Count;

        context.ShoppingListItems.RemoveRange(checkedItems);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Cleared {Count} checked items from ShoppingList {ShoppingListId} for household {HouseholdId}",
            count, shoppingListId, householdId);

        return count;
    }

    public async Task UpdateItemSortOrdersAsync(int householdId, int shoppingListId, List<(int ItemId, int SortOrder, string? Category)> updates, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var itemIds = updates.Select(u => u.ItemId).ToList();
        var items = await context.ShoppingListItems
            .Where(i =>
                i.HouseholdId == householdId &&
                i.ShoppingListId == shoppingListId &&
                itemIds.Contains(i.ItemId))
            .ToListAsync(cancellationToken);

        foreach (var item in items)
        {
            var update = updates.FirstOrDefault(u => u.ItemId == item.ItemId);
            if (update != default)
            {
                item.SortOrder = update.SortOrder;
                if (update.Category != null)
                {
                    item.Category = update.Category;
                }
                item.UpdatedAt = DateTime.UtcNow;
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated sort orders for {Count} items in ShoppingList {ShoppingListId} for household {HouseholdId}",
            updates.Count, shoppingListId, householdId);
    }
}
