using Microsoft.EntityFrameworkCore;
using FamilyCoordinationApp.Data;
using FamilyCoordinationApp.Data.Entities;
using FamilyCoordinationApp.Services.Interfaces;

namespace FamilyCoordinationApp.Services;

public record ConsolidationResult
{
    public string Name { get; set; } = string.Empty;
    public decimal Quantity { get; set; }
    public string Unit { get; set; } = string.Empty;
    public string Category { get; set; } = string.Empty;
    public List<string> SourceRecipes { get; set; } = new();
    public string? OriginalUnits { get; set; }
    public List<string> RecipeIngredientIds { get; set; } = new();
}

public interface IShoppingListGenerator
{
    Task<ShoppingList> GenerateFromMealPlanAsync(int householdId, int mealPlanId, string listName, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default);
    Task<ShoppingList> RegenerateShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);
    Task<List<ConsolidationResult>> ConsolidateIngredientsAsync(List<RecipeIngredient> ingredients, bool autoConsolidate = true);
}

public class ShoppingListGenerator(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IShoppingListService shoppingListService,
    UnitConverter unitConverter,
    ILogger<ShoppingListGenerator> logger) : IShoppingListGenerator
{

    public async Task<ShoppingList> GenerateFromMealPlanAsync(
        int householdId,
        int mealPlanId,
        string listName,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Load meal plan with entries, recipes, and ingredients
        var mealPlan = await context.MealPlans
            .Where(mp => mp.HouseholdId == householdId && mp.MealPlanId == mealPlanId)
            .Include(mp => mp.Entries)
                .ThenInclude(e => e.Recipe)
                    .ThenInclude(r => r!.Ingredients)
            .FirstOrDefaultAsync(cancellationToken);

        if (mealPlan == null)
        {
            throw new InvalidOperationException($"MealPlan {mealPlanId} not found for household {householdId}");
        }

        // Filter entries by date range if provided
        var entries = mealPlan.Entries.AsEnumerable();
        if (startDate.HasValue)
        {
            entries = entries.Where(e => e.Date >= startDate.Value);
        }
        if (endDate.HasValue)
        {
            entries = entries.Where(e => e.Date <= endDate.Value);
        }

        // Collect all RecipeIngredients from filtered entries (skip entries with CustomMealName only)
        var allIngredients = entries
            .Where(e => e.Recipe != null)
            .SelectMany(e => e.Recipe!.Ingredients)
            .ToList();

        // Consolidate ingredients
        var consolidationResults = await ConsolidateIngredientsAsync(allIngredients, autoConsolidate: true);

        // Create shopping list via service
        var shoppingList = await shoppingListService.CreateShoppingListAsync(
            householdId, listName, mealPlanId, cancellationToken);

        // Add items from consolidation results
        foreach (var result in consolidationResults)
        {
            var item = new ShoppingListItem
            {
                HouseholdId = householdId,
                ShoppingListId = shoppingList.ShoppingListId,
                Name = result.Name,
                Quantity = result.Quantity,
                Unit = result.Unit,
                Category = result.Category,
                SourceRecipes = result.SourceRecipes.Count > 0
                    ? string.Join(", ", result.SourceRecipes)
                    : null,
                OriginalUnits = result.OriginalUnits,
                RecipeIngredientIds = result.RecipeIngredientIds.Count > 0
                    ? string.Join(",", result.RecipeIngredientIds)
                    : null,
                IsManuallyAdded = false,
                SortOrder = 0
            };

            await shoppingListService.AddManualItemAsync(item, cancellationToken);
        }

        logger.LogInformation(
            "Generated shopping list {ShoppingListId} from meal plan {MealPlanId} with {ItemCount} items",
            shoppingList.ShoppingListId, mealPlanId, consolidationResults.Count);

        return shoppingList;
    }

    public async Task<ShoppingList> RegenerateShoppingListAsync(
        int householdId,
        int shoppingListId,
        CancellationToken cancellationToken = default)
    {
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Load existing list with items
        var existingList = await shoppingListService.GetShoppingListAsync(householdId, shoppingListId, cancellationToken);
        if (existingList == null)
        {
            throw new InvalidOperationException($"ShoppingList {shoppingListId} not found for household {householdId}");
        }

        if (existingList.MealPlanId == null)
        {
            throw new InvalidOperationException($"ShoppingList {shoppingListId} is not linked to a meal plan");
        }

        // Separate manual items and edited items
        var manualItems = existingList.Items.Where(i => i.IsManuallyAdded).ToList();
        var editedItems = existingList.Items
            .Where(i => i.QuantityDelta.HasValue)
            .ToDictionary(i => NormalizeIngredientName(i.Name));

        // Load linked meal plan
        var mealPlan = await context.MealPlans
            .Where(mp => mp.HouseholdId == householdId && mp.MealPlanId == existingList.MealPlanId)
            .Include(mp => mp.Entries)
                .ThenInclude(e => e.Recipe)
                    .ThenInclude(r => r!.Ingredients)
            .FirstOrDefaultAsync(cancellationToken);

        if (mealPlan == null)
        {
            throw new InvalidOperationException($"MealPlan {existingList.MealPlanId} not found for household {householdId}");
        }

        // Generate fresh consolidation from meal plan
        var allIngredients = mealPlan.Entries
            .Where(e => e.Recipe != null)
            .SelectMany(e => e.Recipe!.Ingredients)
            .ToList();

        var consolidationResults = await ConsolidateIngredientsAsync(allIngredients, autoConsolidate: true);

        // Clear existing non-manual items
        foreach (var item in existingList.Items.Where(i => !i.IsManuallyAdded).ToList())
        {
            await shoppingListService.DeleteItemAsync(householdId, shoppingListId, item.ItemId, cancellationToken);
        }

        // Add new items from consolidation, applying quantity deltas
        foreach (var result in consolidationResults)
        {
            var normalizedName = NormalizeIngredientName(result.Name);
            var quantityDelta = editedItems.ContainsKey(normalizedName)
                ? editedItems[normalizedName].QuantityDelta
                : null;

            var item = new ShoppingListItem
            {
                HouseholdId = householdId,
                ShoppingListId = shoppingListId,
                Name = result.Name,
                Quantity = result.Quantity + (quantityDelta ?? 0),
                Unit = result.Unit,
                Category = result.Category,
                SourceRecipes = result.SourceRecipes.Count > 0
                    ? string.Join(", ", result.SourceRecipes)
                    : null,
                OriginalUnits = result.OriginalUnits,
                RecipeIngredientIds = result.RecipeIngredientIds.Count > 0
                    ? string.Join(",", result.RecipeIngredientIds)
                    : null,
                IsManuallyAdded = false,
                QuantityDelta = quantityDelta,
                SortOrder = 0
            };

            await shoppingListService.AddManualItemAsync(item, cancellationToken);
        }

        // Manual items are already in the list (they weren't deleted)

        logger.LogInformation(
            "Regenerated shopping list {ShoppingListId} from meal plan {MealPlanId} with {ItemCount} items",
            shoppingListId, existingList.MealPlanId, consolidationResults.Count);

        return await shoppingListService.GetShoppingListAsync(householdId, shoppingListId, cancellationToken)
            ?? existingList;
    }

    public async Task<List<ConsolidationResult>> ConsolidateIngredientsAsync(
        List<RecipeIngredient> ingredients,
        bool autoConsolidate = true)
    {
        await Task.CompletedTask; // For async signature consistency

        // Group ingredients by (NormalizedName, Category)
        var groups = ingredients
            .GroupBy(i => new
            {
                NormalizedName = NormalizeIngredientName(i.Name),
                Category = i.Category
            })
            .ToList();

        var results = new List<ConsolidationResult>();

        foreach (var group in groups)
        {
            var items = group.ToList();

            // Find common unit via UnitConverter
            var units = items.Select(i => i.Unit).ToList();
            var commonUnit = unitConverter.FindCommonUnit(units);

            if (commonUnit != null && autoConsolidate)
            {
                // All items can be converted to common unit
                decimal totalQuantity = 0;
                var originalUnits = new List<string>();
                var sourceRecipes = new List<string>();
                var recipeIngredientIds = new List<string>();

                foreach (var item in items)
                {
                    // Convert quantity to common unit
                    var convertedQuantity = item.Quantity.HasValue && !string.IsNullOrWhiteSpace(item.Unit)
                        ? unitConverter.Convert(item.Quantity.Value, item.Unit, commonUnit)
                        : item.Quantity ?? 0;

                    totalQuantity += convertedQuantity;

                    // Track original units
                    if (item.Quantity.HasValue && !string.IsNullOrWhiteSpace(item.Unit))
                    {
                        originalUnits.Add($"{item.Quantity} {item.Unit}");
                    }

                    // Track source recipes
                    if (item.Recipe != null && !string.IsNullOrWhiteSpace(item.Recipe.Name))
                    {
                        sourceRecipes.Add(item.Recipe.Name);
                    }

                    // Track recipe ingredient IDs
                    recipeIngredientIds.Add($"{item.HouseholdId}:{item.RecipeId}:{item.IngredientId}");
                }

                results.Add(new ConsolidationResult
                {
                    Name = items.First().Name,
                    Quantity = totalQuantity,
                    Unit = commonUnit,
                    Category = items.First().Category,
                    SourceRecipes = sourceRecipes.Distinct().ToList(),
                    OriginalUnits = originalUnits.Count > 1 ? string.Join(" + ", originalUnits) : null,
                    RecipeIngredientIds = recipeIngredientIds
                });
            }
            else
            {
                // Keep items separate (incompatible units or imprecise quantities)
                foreach (var item in items)
                {
                    results.Add(new ConsolidationResult
                    {
                        Name = item.Name,
                        Quantity = item.Quantity ?? 0,
                        Unit = item.Unit ?? string.Empty,
                        Category = item.Category,
                        SourceRecipes = item.Recipe != null && !string.IsNullOrWhiteSpace(item.Recipe.Name)
                            ? new List<string> { item.Recipe.Name }
                            : new List<string>(),
                        RecipeIngredientIds = new List<string> { $"{item.HouseholdId}:{item.RecipeId}:{item.IngredientId}" }
                    });
                }
            }
        }

        return results;
    }

    private string NormalizeIngredientName(string name)
    {
        // Remove common descriptors, trim, lowercase
        var normalized = name.ToLowerInvariant().Trim();

        var descriptors = new[] { "fresh", "organic", "chopped", "diced", "minced", "sliced" };
        foreach (var descriptor in descriptors)
        {
            normalized = normalized.Replace(descriptor, "");
        }

        // Remove extra whitespace
        while (normalized.Contains("  "))
        {
            normalized = normalized.Replace("  ", " ");
        }

        return normalized.Trim();
    }
}
