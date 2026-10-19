using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.ChangeTracking;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public class ShoppingListService(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    ILogger<ShoppingListService> logger,
    IHouseholdClock clock,
    IKeyAllocator keys) : IShoppingListService
{
    private const string ListNotFound = "Shopping list not found.";
    private const string ItemNotFound = "Item not found.";

    public async Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken cancellationToken = default)
    {
        return await keys.InsertAsync(
            "ShoppingList",
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                var shoppingList = new ShoppingList
                {
                    ShoppingListId = await keys.NextKeyAsync(context.ShoppingLists, sl => sl.ShoppingListId, cancellationToken),
                    Name = name,
                    MealPlanId = mealPlanId,
                    CreatedAt = clock.UtcNow,
                    IsArchived = false
                };

                context.ShoppingLists.Add(shoppingList);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Created ShoppingList {ShoppingListId} for household {HouseholdId}",
                    shoppingList.ShoppingListId, householdId);

                return shoppingList;
            });
    }

    public async Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.ShoppingLists
            .Where(sl => sl.ShoppingListId == shoppingListId)
            .Include(sl => sl.Items)
                .ThenInclude(i => i.AddedBy)
            .Include(sl => sl.MealPlan)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        return await context.ShoppingLists
            .Where(sl => !sl.IsArchived)
            .OrderByDescending(sl => sl.CreatedAt)
            .Include(sl => sl.Items)
                .ThenInclude(i => i.AddedBy)
            .ToListAsync(cancellationToken);
    }

    public async Task<List<ShoppingList>> GetArchivedShoppingListsAsync(int householdId, bool? favoritesOnly = null, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var query = context.ShoppingLists
            .Where(sl => sl.IsArchived);

        if (favoritesOnly == true)
        {
            query = query.Where(sl => sl.IsFavorite);
        }

        return await query
            .OrderByDescending(sl => sl.IsFavorite)
            .ThenByDescending(sl => sl.CreatedAt)
            .Include(sl => sl.Items)
            .ToListAsync(cancellationToken);
    }

    public async Task RestoreShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var shoppingList = await context.ShoppingLists
            .FirstOrDefaultAsync(sl => sl.ShoppingListId == shoppingListId, cancellationToken)
            ?? throw new NotFoundException(ListNotFound, $"Shopping list {shoppingListId} not found");

        shoppingList.IsArchived = false;
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Restored ShoppingList {ShoppingListId} for household {HouseholdId}",
            shoppingListId, householdId);
    }

    public async Task DeleteShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var shoppingList = await context.ShoppingLists
            .Include(sl => sl.Items)
            .FirstOrDefaultAsync(sl => sl.ShoppingListId == shoppingListId, cancellationToken)
            ?? throw new NotFoundException(ListNotFound, $"Shopping list {shoppingListId} not found");

        // Remove all items first
        context.ShoppingListItems.RemoveRange(shoppingList.Items);

        // Then remove the list
        context.ShoppingLists.Remove(shoppingList);

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Permanently deleted ShoppingList {ShoppingListId} for household {HouseholdId}",
            shoppingListId, householdId);
    }

    public async Task<ShoppingList> RenameShoppingListAsync(int householdId, int shoppingListId, string newName, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var shoppingList = await context.ShoppingLists
            .FirstOrDefaultAsync(sl => sl.ShoppingListId == shoppingListId, cancellationToken)
            ?? throw new NotFoundException(ListNotFound, $"Shopping list {shoppingListId} not found");

        shoppingList.Name = newName.Trim();
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Renamed ShoppingList {ShoppingListId} to '{NewName}' for household {HouseholdId}",
            shoppingListId, newName, householdId);

        return shoppingList;
    }

    public async Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken cancellationToken = default)
    {
        return await keys.InsertAsync(
            "ShoppingListItem",
            async (attempt) =>
            {
                await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

                item.ItemId = await keys.NextKeyAsync(
                    context.ShoppingListItems.Where(i => i.ShoppingListId == item.ShoppingListId), i => i.ItemId, cancellationToken);
                item.AddedAt = clock.UtcNow;
                item.UpdatedAt = clock.UtcNow;
                item.IsChecked = false;

                context.ShoppingListItems.Add(item);
                await context.SaveChangesAsync(cancellationToken);

                logger.LogInformation("Added manual item {ItemId} to ShoppingList {ShoppingListId} for household {HouseholdId}",
                    item.ItemId, item.ShoppingListId, item.HouseholdId);

                return item;
            });
    }

    /// <summary>
    /// Updates a shopping list item checked against the client's <paramref name="version"/> (quest 8cf3c806, operator
    /// answer c): a stale or missing (<see cref="ConcurrencySave.MissingToken"/>) token throws
    /// <see cref="ConcurrencyConflictException"/> (→ 409), like every other client-token write. The old "checked wins"
    /// merge-retry is gone; the client reloads on 409.
    /// </summary>
    /// <exception cref="NotFoundException">The item is gone.</exception>
    public async Task UpdateItemAsync(ShoppingListItem item, uint version, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existing = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.ShoppingListId == item.ShoppingListId &&
                i.ItemId == item.ItemId, cancellationToken);

        if (existing == null)
        {
            throw new NotFoundException(ItemNotFound, $"Item {item.ItemId} not found in list {item.ShoppingListId}");
        }

        // Apply changes from the passed-in item. This copy is a whitelist — a field left off
        // it is silently dropped on the floor (QuantityDelta was, which made regenerate lose
        // every quantity edit; council round 1 on PR #101).
        existing.IsChecked = item.IsChecked;
        existing.CheckedAt = item.CheckedAt;
        existing.Name = item.Name;
        existing.Quantity = item.Quantity;
        existing.QuantityDelta = item.QuantityDelta;
        existing.Unit = item.Unit;
        existing.Category = item.Category;
        // A touch, kept on purpose: a PATCH that changes nothing must still put the row in the UPDATE so the version
        // check runs. The write step stamps UpdatedByUserId (the caller; quest e8bf9e1d).
        existing.UpdatedAt = clock.UtcNow;

        await context.SaveWithConcurrencyAsync(existing, version, cancellationToken);

        // Copy the saved row back onto the caller's object: the endpoint answers with `item`, so without this the new
        // xmin Version never reaches the client. Same whitelist as the apply block above, plus the audit columns and
        // Version — a field left off it is answered stale.
        item.IsChecked = existing.IsChecked;
        item.CheckedAt = existing.CheckedAt;
        item.Name = existing.Name;
        item.Quantity = existing.Quantity;
        item.QuantityDelta = existing.QuantityDelta;
        item.Unit = existing.Unit;
        item.Category = existing.Category;
        item.UpdatedByUserId = existing.UpdatedByUserId;
        item.UpdatedAt = existing.UpdatedAt;
        item.Version = existing.Version;

        logger.LogInformation("Updated item {ItemId} in ShoppingList {ShoppingListId} for household {HouseholdId}",
            item.ItemId, item.ShoppingListId, item.HouseholdId);
    }

    public async Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var item = await context.ShoppingListItems
            .FirstOrDefaultAsync(i =>
                i.ShoppingListId == shoppingListId &&
                i.ItemId == itemId, cancellationToken);

        if (item == null)
        {
            throw new NotFoundException("Item not found.", $"Item {itemId} not found in ShoppingList {shoppingListId} for household {householdId}");
        }

        context.ShoppingListItems.Remove(item);
        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Deleted item {ItemId} from ShoppingList {ShoppingListId} for household {HouseholdId}",
            itemId, shoppingListId, householdId);
    }

    public async Task<ShoppingList> ToggleFavoriteAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var shoppingList = await context.ShoppingLists
            .FirstOrDefaultAsync(sl =>
                sl.ShoppingListId == shoppingListId, cancellationToken);

        if (shoppingList == null)
        {
            throw new NotFoundException(ListNotFound, $"ShoppingList {shoppingListId} not found for household {householdId}");
        }

        shoppingList.IsFavorite = !shoppingList.IsFavorite;

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Toggled ShoppingList {ShoppingListId} favorite state to {IsFavorite} for household {HouseholdId}",
            shoppingListId, shoppingList.IsFavorite, householdId);

        return shoppingList;
    }

    public async Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, int limit = 10, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var lower = prefix.ToLowerInvariant();

        // Search ALL shopping list items (including archived lists) for autocomplete
        // Prioritize StartsWith matches, fallback to Contains
        var startsWithMatches = await context.ShoppingListItems
            .Where(i => i.Name.ToLower().StartsWith(lower))
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
            .Where(i => i.Name.ToLower().Contains(lower)
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
                sl.ShoppingListId == shoppingListId, cancellationToken);

        if (shoppingList == null)
        {
            throw new NotFoundException(ListNotFound, $"ShoppingList {shoppingListId} not found for household {householdId}");
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

    public async Task<IReadOnlyList<(int ItemId, uint VersionBefore, uint Version)>> UpdateItemSortOrdersAsync(int householdId, int shoppingListId, List<(int ItemId, int SortOrder, string? Category)> updates, CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var itemIds = updates.Select(u => u.ItemId).ToList();
        var items = await context.ShoppingListItems
            .Where(i =>
                i.ShoppingListId == shoppingListId &&
                itemIds.Contains(i.ItemId))
            .ToListAsync(cancellationToken);

        // The token each row had when this request read it: the client adopts the post-save token only where this
        // equals the token it held, so a row someone else changed since its last read is never given a token that
        // would let its stale content overwrite that change (quest 8cf3c806).
        var versionsBefore = items.ToDictionary(i => i.ItemId, i => i.Version);

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
                // No UpdatedAt touch: only a row whose order or category really changed is written, so a reorder
                // advances no other item's token (quest 8cf3c806). The write step stamps UpdatedAt and
                // UpdatedByUserId (the caller) on the rows that are written (quest e8bf9e1d, answer d).
            }
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation("Updated sort orders for {Count} items in ShoppingList {ShoppingListId} for household {HouseholdId}",
            updates.Count, shoppingListId, householdId);

        // The tokens before and after the save, so the client's next PATCH on a moved item isn't stale. The save is
        // checked against the tokens read above, so a write landing in between is a 409, never folded in silently.
        return [.. items.Select(i => (i.ItemId, versionsBefore[i.ItemId], i.Version))];
    }
}
