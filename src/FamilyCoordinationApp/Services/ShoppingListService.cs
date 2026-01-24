using Microsoft.EntityFrameworkCore;
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
