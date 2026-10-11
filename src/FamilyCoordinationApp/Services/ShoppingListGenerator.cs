using System.Globalization;
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

/// <summary>
/// A recipe ingredient paired with the factor the meal it was planned for asks for. 1 means "as the recipe is
/// written". The pairing has to be carried into consolidation because grouping by ingredient name destroys the
/// entry association, and two entries can plan the SAME recipe at different servings.
/// </summary>
public readonly record struct ScaledIngredient(RecipeIngredient Ingredient, decimal Factor);

public interface IShoppingListGenerator
{
    Task<ShoppingList> GenerateFromMealPlanAsync(int householdId, int mealPlanId, string listName, DateOnly? startDate = null, DateOnly? endDate = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Rebuild the generated rows from the list's linked meal plan — the WHOLE plan week, regardless of
    /// the date range the list was originally generated with (the range is not persisted; see the
    /// past-lists spec-lite). Atomic (one SaveChanges); manual items untouched; checked state, sort
    /// position and each edited item's QuantityDelta carry onto the fresh rows by the consolidator's
    /// (normalized name, category) identity. Wired at POST /{listId}/actions/regenerate.
    /// </summary>
    Task<ShoppingList> RegenerateShoppingListAsync(int householdId, int shoppingListId, CancellationToken cancellationToken = default);

    /// <summary>Consolidate ingredients as written — equivalent to every factor being 1.</summary>
    Task<List<ConsolidationResult>> ConsolidateIngredientsAsync(List<RecipeIngredient> ingredients, bool autoConsolidate = true);

    /// <summary>Consolidate ingredients after scaling each by the factor its planned meal asks for.</summary>
    Task<List<ConsolidationResult>> ConsolidateScaledIngredientsAsync(List<ScaledIngredient> ingredients, bool autoConsolidate = true);
}

public class ShoppingListGenerator(
    IDbContextFactory<ApplicationDbContext> dbFactory,
    IShoppingListService shoppingListService,
    UnitConverter unitConverter,
    ILogger<ShoppingListGenerator> logger,
    IHouseholdClock clock) : IShoppingListGenerator
{

    public Task<ShoppingList> GenerateFromMealPlanAsync(
        int householdId,
        int mealPlanId,
        string listName,
        DateOnly? startDate = null,
        DateOnly? endDate = null,
        CancellationToken cancellationToken = default) =>
        // Retry wrapper: the list id is assigned max+1 in memory, so a concurrent create can collide on the
        // composite key (the same hazard and remedy as CreateShoppingListAsync, which this used to call).
        IdGenerationHelper.ExecuteWithRetryAsync(
            _ => GenerateOnceAsync(householdId, mealPlanId, listName, startDate, endDate, cancellationToken),
            logger,
            "ShoppingList");

    private async Task<ShoppingList> GenerateOnceAsync(
        int householdId,
        int mealPlanId,
        string listName,
        DateOnly? startDate,
        DateOnly? endDate,
        CancellationToken cancellationToken)
    {
        // One context, one SaveChanges (quest b942b943): the list and all its items commit together. This used to
        // create the list through the service and then save once per item, so a failure partway (a column
        // overflow, say) left a list holding some of its items.
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        // Load meal plan with entries, recipes, and ingredients
        var mealPlan = await context.MealPlans
            .Where(mp => mp.MealPlanId == mealPlanId)
            .Include(mp => mp.Entries)
                .ThenInclude(e => e.Recipe)
                    .ThenInclude(r => r!.Ingredients)
            .FirstOrDefaultAsync(cancellationToken);

        if (mealPlan == null)
        {
            throw new NotFoundException("Meal plan not found.", $"MealPlan {mealPlanId} not found for household {householdId}");
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

        // Collect all RecipeIngredients from filtered entries (skip entries with CustomMealName only), each
        // carrying the factor its own entry asks for — the same recipe planned twice at different servings
        // contributes twice, at different scales.
        var allIngredients = entries
            .Where(e => e.Recipe != null)
            .SelectMany(e => e.Recipe!.Ingredients.Select(i =>
                new ScaledIngredient(i, ScaleFactorFor(e.Servings, e.Recipe!.Servings))))
            .ToList();

        // Consolidate ingredients
        var consolidationResults = await ConsolidateScaledIngredientsAsync(allIngredients, autoConsolidate: true);

        var maxListId = await context.ShoppingLists
            .MaxAsync(sl => (int?)sl.ShoppingListId, cancellationToken) ?? 0;
        var now = clock.UtcNow;

        var shoppingList = new ShoppingList
        {
            ShoppingListId = maxListId + 1,
            Name = listName,
            MealPlanId = mealPlanId,
            CreatedAt = now,
            IsArchived = false
        };
        context.ShoppingLists.Add(shoppingList);

        // The list is new, so its item ids start at 1.
        var itemId = 0;
        foreach (var result in consolidationResults)
        {
            // No HouseholdId: the write step stamps the new list and then its items (parents before children).
            shoppingList.Items.Add(new ShoppingListItem
            {
                ShoppingListId = shoppingList.ShoppingListId,
                ItemId = ++itemId,
                Name = result.Name,
                Quantity = result.Quantity,
                Unit = result.Unit,
                Category = result.Category,
                SourceRecipes = JoinSourceRecipes(result.SourceRecipes),
                OriginalUnits = result.OriginalUnits,
                RecipeIngredientIds = JoinRecipeIngredientIds(result.RecipeIngredientIds),
                IsManuallyAdded = false,
                IsChecked = false,
                SortOrder = 0,
                AddedAt = now,
                UpdatedAt = now
            });
        }

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Generated shopping list {ShoppingListId} from meal plan {MealPlanId} with {ItemCount} items",
            shoppingList.ShoppingListId, mealPlanId, consolidationResults.Count);

        return shoppingList;
    }

    public Task<ShoppingList> RegenerateShoppingListAsync(
        int householdId,
        int shoppingListId,
        CancellationToken cancellationToken = default) =>
        // Retry wrapper: item ids are assigned max+1 in memory, so a concurrent manual add can
        // collide on the composite key — same hazard (and same remedy) as the sibling writes.
        IdGenerationHelper.ExecuteWithRetryAsync(
            _ => RegenerateOnceAsync(householdId, shoppingListId, cancellationToken),
            logger,
            "ShoppingListRegenerate");

    private async Task<ShoppingList> RegenerateOnceAsync(
        int householdId,
        int shoppingListId,
        CancellationToken cancellationToken)
    {
        // One context, one SaveChanges: the rebuild must be atomic. Going through the item-level
        // service calls would save once per item on separate contexts, and a failure mid-loop would
        // leave the list half-rebuilt.
        await using var context = await dbFactory.CreateDbContextAsync(cancellationToken);

        var existingList = await context.ShoppingLists
            .Where(sl => sl.ShoppingListId == shoppingListId)
            .Include(sl => sl.Items)
            .FirstOrDefaultAsync(cancellationToken)
            ?? throw new NotFoundException(
                "Shopping list not found.", $"ShoppingList {shoppingListId} not found for household {householdId}");

        if (existingList.MealPlanId == null)
        {
            throw new ConflictException(
                "This list is not linked to a meal plan.", $"ShoppingList {shoppingListId} is not linked to a meal plan");
        }

        // Outgoing generated rows keyed by the CONSOLIDATOR'S identity — (normalized name, category),
        // the same pair ConsolidateScaledIngredientsAsync groups by — so same-name rows in different
        // categories each keep their own checked state / sort position / quantity edit. Genuine
        // duplicates within one identity resolve to the lowest ItemId (deterministic, oldest row).
        var outgoing = existingList.Items
            .Where(i => !i.IsManuallyAdded)
            .GroupBy(i => (Name: NormalizeIngredientName(i.Name), i.Category))
            .ToDictionary(g => g.Key, g => g.OrderBy(i => i.ItemId).First());

        var mealPlan = await context.MealPlans
            .Where(mp => mp.MealPlanId == existingList.MealPlanId)
            .Include(mp => mp.Entries)
                .ThenInclude(e => e.Recipe)
                    .ThenInclude(r => r!.Ingredients)
            .FirstOrDefaultAsync(cancellationToken)
            // The list's link survives but the plan row is gone (deleted since): a client-resolvable state (409).
            ?? throw new ConflictException(
                "The linked meal plan no longer exists.", $"MealPlan {existingList.MealPlanId} not found for household {householdId}");

        // Generate fresh consolidation from meal plan, honouring each entry's servings the same way generation
        // does — so a regenerate after changing an entry's servings reflects the change.
        var allIngredients = mealPlan.Entries
            .Where(e => e.Recipe != null)
            .SelectMany(e => e.Recipe!.Ingredients.Select(i =>
                new ScaledIngredient(i, ScaleFactorFor(e.Servings, e.Recipe!.Servings))))
            .ToList();

        var consolidationResults = await ConsolidateScaledIngredientsAsync(allIngredients, autoConsolidate: true);

        var nextItemId = existingList.Items.Count > 0 ? existingList.Items.Max(i => i.ItemId) : 0;

        context.ShoppingListItems.RemoveRange(existingList.Items.Where(i => !i.IsManuallyAdded));

        foreach (var result in consolidationResults)
        {
            outgoing.TryGetValue((NormalizeIngredientName(result.Name), result.Category), out var previous);
            var quantityDelta = previous?.QuantityDelta;

            context.ShoppingListItems.Add(new ShoppingListItem
            {
                ShoppingListId = shoppingListId,
                ItemId = ++nextItemId,
                Name = result.Name,
                // Floor at zero: a carried negative edit larger than the fresh quantity must not
                // produce a negative line.
                Quantity = Math.Max(0, result.Quantity + (quantityDelta ?? 0)),
                Unit = result.Unit,
                Category = result.Category,
                SourceRecipes = JoinSourceRecipes(result.SourceRecipes),
                OriginalUnits = result.OriginalUnits,
                RecipeIngredientIds = JoinRecipeIngredientIds(result.RecipeIngredientIds),
                IsManuallyAdded = false,
                QuantityDelta = quantityDelta,
                IsChecked = previous?.IsChecked ?? false,
                CheckedAt = previous?.CheckedAt,
                SortOrder = previous?.SortOrder ?? 0,
                AddedAt = clock.UtcNow,
                UpdatedAt = clock.UtcNow
            });
        }

        // Manual items were never removed and are untouched.

        await context.SaveChangesAsync(cancellationToken);

        logger.LogInformation(
            "Regenerated shopping list {ShoppingListId} from meal plan {MealPlanId} with {ItemCount} items",
            shoppingListId, existingList.MealPlanId, consolidationResults.Count);

        return await shoppingListService.GetShoppingListAsync(householdId, shoppingListId, cancellationToken)
            ?? existingList;
    }

    /// <summary>
    /// What one planned meal asks for, relative to the recipe as written. 1 whenever there is nothing to scale
    /// against — no override, or a recipe that never declared its own yield. Both are the common case, and both
    /// must leave quantities exactly as they were before this feature existed.
    /// </summary>
    public static decimal ScaleFactorFor(int? entryServings, int? recipeServings) =>
        entryServings is > 0 && recipeServings is > 0
            ? (decimal)entryServings.Value / recipeServings.Value
            : 1m;

    /// <summary>Distinct recipe names, bounded to the column with a visible "… (+N more)" marker.</summary>
    private static string? JoinSourceRecipes(List<string> sourceRecipes) =>
        sourceRecipes.Count > 0
            ? BoundedJoin.Join(sourceRecipes, ", ", FieldLengths.ShoppingListItem.SourceRecipes)
            : null;

    /// <summary>
    /// Distinct ingredient identities, bounded to the column by dropping whole ids past it: a marker would not
    /// parse as an id. Nothing in src reads this column back.
    /// </summary>
    private static string? JoinRecipeIngredientIds(List<string> recipeIngredientIds) =>
        recipeIngredientIds.Count > 0
            ? BoundedJoin.Join(recipeIngredientIds, ",", FieldLengths.ShoppingListItem.RecipeIngredientIds, marker: false)
            : null;

    /// <summary>Quantities are persisted as <c>decimal(10,2)</c>; round here so the computed value is the stored one.</summary>
    private static decimal Round(decimal quantity) => Math.Round(quantity, 2, MidpointRounding.AwayFromZero);

    public Task<List<ConsolidationResult>> ConsolidateIngredientsAsync(
        List<RecipeIngredient> ingredients,
        bool autoConsolidate = true) =>
        ConsolidateScaledIngredientsAsync(
            ingredients.Select(i => new ScaledIngredient(i, 1m)).ToList(), autoConsolidate);

    public async Task<List<ConsolidationResult>> ConsolidateScaledIngredientsAsync(
        List<ScaledIngredient> ingredients,
        bool autoConsolidate = true)
    {
        await Task.CompletedTask; // For async signature consistency

        // Group ingredients by (NormalizedName, Category)
        var groups = ingredients
            .GroupBy(si => new
            {
                NormalizedName = NormalizeIngredientName(si.Ingredient.Name),
                Category = si.Ingredient.Category
            })
            .ToList();

        var results = new List<ConsolidationResult>();

        foreach (var group in groups)
        {
            var items = group.ToList();

            // Find common unit via UnitConverter
            var units = items.Select(si => si.Ingredient.Unit).ToList();
            var commonUnit = unitConverter.FindCommonUnit(units);

            // A group with no units at all ("2 eggs" planned twice) has nothing to convert but
            // everything to sum — consolidate it under the empty unit rather than emitting one
            // line per source.
            var unitless = commonUnit == null && units.All(string.IsNullOrWhiteSpace);

            if ((commonUnit != null || unitless) && autoConsolidate)
            {
                // All items can be converted to common unit
                decimal totalQuantity = 0;
                var originalUnits = new List<string>();
                var sourceRecipes = new List<string>();
                var recipeIngredientIds = new List<string>();

                foreach (var scaled in items)
                {
                    var item = scaled.Ingredient;

                    // Scale to what the meal asks for BEFORE converting units, so the conversion and the sum
                    // both operate on the amount actually being cooked.
                    var scaledQuantity = item.Quantity.HasValue ? item.Quantity.Value * scaled.Factor : (decimal?)null;

                    var convertedQuantity = scaledQuantity.HasValue && !string.IsNullOrWhiteSpace(item.Unit)
                        ? unitConverter.Convert(scaledQuantity.Value, item.Unit, commonUnit)
                        : scaledQuantity ?? 0;

                    totalQuantity += convertedQuantity;

                    // Track original units — the per-source amounts this list is actually built from.
                    // "0.##" rather than the raw decimal: scaling changes a value's SCALE as well as its
                    // magnitude (2 × 0.5 is 1.0, not 1), and the trailing zero would surface in the breakdown.
                    if (scaledQuantity.HasValue && !string.IsNullOrWhiteSpace(item.Unit))
                    {
                        originalUnits.Add(
                            $"{Round(scaledQuantity.Value).ToString("0.##", CultureInfo.InvariantCulture)} {item.Unit}");
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
                    Name = items.First().Ingredient.Name,
                    Quantity = Round(totalQuantity),
                    Unit = commonUnit ?? string.Empty,
                    Category = items.First().Ingredient.Category,
                    SourceRecipes = sourceRecipes.Distinct().ToList(),
                    // Repeats collapse ("20 × 1 cup") and the join is bounded to its column: one staple planned in
                    // every slot of the week used to join one part per slot and overflow it (b942b943).
                    OriginalUnits = originalUnits.Count > 1
                        ? BoundedJoin.Join(
                            BoundedJoin.CollapseRepeats(originalUnits), " + ", FieldLengths.ShoppingListItem.OriginalUnits)
                        : null,
                    // Distinct for the same reason SourceRecipes is: one recipe planned twice in a week
                    // contributes the same RecipeIngredient rows twice, and these are identities, not counts.
                    RecipeIngredientIds = recipeIngredientIds.Distinct().ToList()
                });
            }
            else
            {
                // Keep items separate (incompatible units or imprecise quantities)
                foreach (var scaled in items)
                {
                    var item = scaled.Ingredient;

                    results.Add(new ConsolidationResult
                    {
                        Name = item.Name,
                        Quantity = Round((item.Quantity ?? 0) * scaled.Factor),
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
