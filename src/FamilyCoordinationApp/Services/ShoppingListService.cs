using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;

namespace FamilyCoordinationApp.Services;

public interface IShoppingListService
{
    Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken ct = default);
    Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken ct = default);
    Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken ct = default);
    Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken ct = default);
    Task UpdateItemAsync(ShoppingListItem item, CancellationToken ct = default);
    Task<(bool Success, bool WasConflict, string? ConflictMessage)> UpdateItemWithConcurrencyAsync(ShoppingListItem item, CancellationToken ct = default);
    Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken ct = default);
    Task<ShoppingListItem> ToggleItemCheckedAsync(int householdId, int shoppingListId, int itemId, CancellationToken ct = default);
    Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, int limit = 10, CancellationToken ct = default);
    Task ArchiveShoppingListAsync(int householdId, int shoppingListId, CancellationToken ct = default);
    Task<int> ClearCheckedItemsAsync(int householdId, int shoppingListId, CancellationToken ct = default);
}

public class ShoppingListService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ShoppingListService> logger) : IShoppingListService
{

    public async Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var maxId = await context.ShoppingLists
            .Where(sl => sl.HouseholdId == householdId)
            .MaxAsync(sl => (int?)sl.ShoppingListId, ct) ?? 0;

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
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Created ShoppingList {ShoppingListId} for household {HouseholdId}",
            shoppingList.ShoppingListId, householdId);

        return shoppingList;
    }

    public async Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        return await context.ShoppingLists
            .Where(sl => sl.HouseholdId == householdId && sl.ShoppingListId == shoppingListId)
            .Include(sl => sl.Items)
                .ThenInclude(i => i.AddedBy)
            .Include(sl => sl.MealPlan)
            .FirstOrDefaultAsync(ct);
    }

    public async Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        return await context.ShoppingLists
            .Where(sl => sl.HouseholdId == householdId && !sl.IsArchived)
            .OrderByDescending(sl => sl.CreatedAt)
            .Include(sl => sl.Items)
                .ThenInclude(i => i.AddedBy)
            .ToListAsync(ct);
    }

    public async Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var maxItemId = await context.ShoppingListItems
            .Where(i => i.HouseholdId == item.HouseholdId && i.ShoppingListId == item.ShoppingListId)
            .MaxAsync(i => (int?)i.ItemId, ct) ?? 0;

        item.ItemId = maxItemId + 1;
        item.AddedAt = DateTime.UtcNow;
        item.IsChecked = false;

        context.ShoppingListItems.Add(item);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Added manual item {ItemId} to ShoppingList {ShoppingListId} for household {HouseholdId}",
            item.ItemId, item.ShoppingListId, item.HouseholdId);

        return item;
    }

    public async Task UpdateItemAsync(ShoppingListItem item, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var existing = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.HouseholdId == item.HouseholdId &&
                i.ShoppingListId == item.ShoppingListId &&
                i.ItemId == item.ItemId, ct);

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

        await context.SaveChangesAsync(ct);

        logger.LogInformation("Updated item {ItemId} in ShoppingList {ShoppingListId} for household {HouseholdId}",
            item.ItemId, item.ShoppingListId, item.HouseholdId);
    }

    /// <summary>
    /// Updates a shopping list item with optimistic concurrency handling.
    /// Uses "checked wins" strategy: if either user checked the item, it stays checked.
    /// </summary>
    /// <returns>True if update succeeded, false if item was deleted by another user</returns>
    public async Task<(bool Success, bool WasConflict, string? ConflictMessage)> UpdateItemWithConcurrencyAsync(ShoppingListItem item, CancellationToken ct = default)
    {
        const int maxRetries = 3;
        var retries = 0;
        var wasConflict = false;
        string? conflictMessage = null;

        while (retries < maxRetries)
        {
            try
            {
                await using var context = await dbFactory.CreateDbContextAsync(ct);

                // Attach the item to this context
                context.ShoppingListItems.Attach(item);
                context.Entry(item).State = EntityState.Modified;

                // Set tracking fields
                item.UpdatedAt = DateTime.UtcNow;

                await context.SaveChangesAsync(ct);

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
                        var databaseValues = await entry.GetDatabaseValuesAsync(ct);

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

    public async Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var item = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.HouseholdId == householdId &&
                i.ShoppingListId == shoppingListId &&
                i.ItemId == itemId, ct);

        if (item == null)
        {
            throw new InvalidOperationException($"Item {itemId} not found in ShoppingList {shoppingListId} for household {householdId}");
        }

        context.ShoppingListItems.Remove(item);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Deleted item {ItemId} from ShoppingList {ShoppingListId} for household {HouseholdId}",
            itemId, shoppingListId, householdId);
    }

    public async Task<ShoppingListItem> ToggleItemCheckedAsync(int householdId, int shoppingListId, int itemId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var item = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.HouseholdId == householdId &&
                i.ShoppingListId == shoppingListId &&
                i.ItemId == itemId, ct);

        if (item == null)
        {
            throw new InvalidOperationException($"Item {itemId} not found in ShoppingList {shoppingListId} for household {householdId}");
        }

        item.IsChecked = !item.IsChecked;
        item.CheckedAt = item.IsChecked ? DateTime.UtcNow : null;

        await context.SaveChangesAsync(ct);

        logger.LogInformation("Toggled item {ItemId} checked state to {IsChecked} in ShoppingList {ShoppingListId} for household {HouseholdId}",
            itemId, item.IsChecked, shoppingListId, householdId);

        return item;
    }

    public async Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, int limit = 10, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

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
            .ToListAsync(ct);

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
            .ToListAsync(ct);

        return startsWithMatches.Concat(containsMatches).ToList();
    }

    public async Task ArchiveShoppingListAsync(int householdId, int shoppingListId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var shoppingList = await context.ShoppingLists
            .FirstOrDefaultAsync(sl =>
                sl.HouseholdId == householdId &&
                sl.ShoppingListId == shoppingListId, ct);

        if (shoppingList == null)
        {
            throw new InvalidOperationException($"ShoppingList {shoppingListId} not found for household {householdId}");
        }

        shoppingList.IsArchived = true;
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Archived ShoppingList {ShoppingListId} for household {HouseholdId}",
            shoppingListId, householdId);
    }

    public async Task<int> ClearCheckedItemsAsync(int householdId, int shoppingListId, CancellationToken ct = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(ct);

        var checkedItems = await context.ShoppingListItems
            .Where(i =>
                i.HouseholdId == householdId &&
                i.ShoppingListId == shoppingListId &&
                i.IsChecked)
            .ToListAsync(ct);

        var count = checkedItems.Count;

        context.ShoppingListItems.RemoveRange(checkedItems);
        await context.SaveChangesAsync(ct);

        logger.LogInformation("Cleared {Count} checked items from ShoppingList {ShoppingListId} for household {HouseholdId}",
            count, shoppingListId, householdId);

        return count;
    }
}
