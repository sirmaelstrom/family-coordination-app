# Phase 4: Shopping List Core - Research

**Researched:** 2026-01-24
**Domain:** Shopping list generation from meal plans with ingredient consolidation, drag-drop reordering, and in-store shopping workflow
**Confidence:** MEDIUM

## Summary

Phase 4 implements shopping list generation from weekly meal plans with automatic ingredient consolidation, category grouping, and in-store shopping support. The data model already exists (ShoppingList, ShoppingListItem entities with composite keys). Key technical domains investigated: unit conversion libraries for ingredient consolidation, drag-drop UI for list reordering, autocomplete for manual item entry, and snackbar undo patterns.

**Critical finding:** The project already includes **Fractions** (8.3.2) for fraction math and **blazor-dragdrop** (2.6.1) for drag-drop operations. These handle two major requirements: unit conversion math and list reordering UI. Ingredient consolidation requires semantic matching (same ingredient, compatible units) plus unit normalization. MudBlazor provides autocomplete, snackbars, and FAB components for the shopping workflow.

**Unit conversion insight:** Don't use heavyweight libraries like UnitsNet for this domain. Cooking measurements need simple conversions (tsp→tbsp→cup, oz→lb, g→kg) that can be implemented with lookup tables. The Fractions library already handles fractional quantities ("1/2 cup" + "3/4 cup" = "1 1/4 cups").

**Primary recommendation:** Build a lightweight UnitConverter service with conversion tables for cooking units. Use string matching with normalization (trim, lowercase) for identical ingredient detection. Leverage blazor-dragdrop for reordering, MudAutocomplete for manual item entry with history-based suggestions, and MudSnackbar with action buttons for undo support. Track item frequency in database (add PurchaseCount field) for popularity-based initial ordering.

## Standard Stack

The established libraries/tools for shopping list management in Blazor:

### Core

| Library | Version | Purpose | Why Standard |
|---------|---------|---------|--------------|
| MudBlazor | 8.15.0 | UI components (Autocomplete, Snackbar, FAB, Dialog) | Already in use, provides all UI primitives needed |
| blazor-dragdrop | 2.6.1 | Drag-drop list reordering | Already installed, simpler API than MudDropZone for list reordering |
| Fractions | 8.3.2 | Fraction arithmetic for quantities | Already installed, handles "1/2 + 3/4" math natively |
| Microsoft.EntityFrameworkCore | 10.0.x | Data access with composite keys | Existing pattern, ShoppingList entities already configured |

### Supporting

| Library | Version | Purpose | When to Use |
|---------|---------|---------|-------------|
| FuzzySharp | Latest | Fuzzy string matching for ingredients | Only if simple string normalization fails for consolidation (OPTIONAL, evaluate during implementation) |

### Alternatives Considered

| Instead of | Could Use | Tradeoff |
|------------|-----------|----------|
| Custom unit conversion | UnitsNet | UnitsNet supports 100+ unit types (engineering, physics). Overkill for 15 cooking units. Custom table is 50 lines. |
| blazor-dragdrop | MudDropZone | MudDropZone requires MudDropContainer wrapper, more setup. blazor-dragdrop is simpler for single-list reordering. |
| String normalization | FuzzySharp | Fuzzy matching catches typos but adds complexity. Start with exact matching (normalized), add fuzzy only if needed. |
| Autocomplete from history | External search service | Shopping items are household-specific, small dataset (<1000 items). Database autocomplete is sufficient. |

**Installation:**
All required packages already installed. No additional dependencies needed.

## Architecture Patterns

### Recommended Project Structure

```
Components/
├── Pages/
│   └── ShoppingList.razor              # Main shopping list page
├── ShoppingList/
│   ├── ShoppingListView.razor          # List display with drag-drop
│   ├── ShoppingListItem.razor          # Individual item row (tap to check)
│   ├── AddItemDialog.razor             # Quick add dialog (FAB opens this)
│   ├── CategorySection.razor           # Collapsible category group
│   └── StoreLayoutSelector.razor       # Preset picker dialog
Services/
├── ShoppingListService.cs              # CRUD operations for lists and items
├── ShoppingListGenerator.cs            # Generate list from meal plan with consolidation
├── UnitConverter.cs                    # Unit conversion table and normalization
└── IngredientMatcher.cs                # Semantic matching for consolidation
Data/
└── Migrations/
    └── AddShoppingListItemMetadata.cs  # Add PurchaseCount, LastPurchasedAt for frequency tracking
```

### Pattern 1: Ingredient Consolidation with Unit Normalization

**What:** When generating shopping list from meal plan, consolidate duplicate ingredients by normalizing units to common base, summing quantities, and tracking source recipes.

**When to use:** Every time shopping list is generated from meal plan.

**Example:**
```csharp
// ShoppingListGenerator.cs
public class ConsolidationResult
{
    public string Name { get; set; }            // "Milk"
    public decimal Quantity { get; set; }       // 2.5
    public string Unit { get; set; }            // "cups"
    public string Category { get; set; }        // "Dairy"
    public List<string> SourceRecipes { get; set; } = new(); // ["Pancakes", "Mac & Cheese"]
    public string? OriginalUnits { get; set; }  // "1 cup + 12 oz" (if converted)
}

public async Task<List<ConsolidationResult>> ConsolidateIngredientsAsync(
    List<RecipeIngredient> ingredients,
    bool autoConsolidate = true)
{
    var groups = ingredients
        .GroupBy(i => new {
            Name = NormalizeIngredientName(i.Name),
            Category = i.Category
        })
        .ToList();

    var results = new List<ConsolidationResult>();

    foreach (var group in groups)
    {
        var items = group.ToList();

        // Try to find common unit
        var commonUnit = FindCommonUnit(items.Select(i => i.Unit).ToList());

        if (commonUnit != null && autoConsolidate)
        {
            // All items can be converted to common unit
            decimal totalQuantity = 0;
            var originalUnits = new List<string>();

            foreach (var item in items)
            {
                var converted = _unitConverter.Convert(
                    item.Quantity ?? 0,
                    item.Unit,
                    commonUnit);
                totalQuantity += converted;

                if (item.Quantity.HasValue && !string.IsNullOrEmpty(item.Unit))
                {
                    originalUnits.Add($"{item.Quantity} {item.Unit}");
                }
            }

            results.Add(new ConsolidationResult
            {
                Name = items.First().Name,
                Quantity = totalQuantity,
                Unit = commonUnit,
                Category = items.First().Category,
                SourceRecipes = items.Select(i => i.Recipe?.Name ?? "Manual").Distinct().ToList(),
                OriginalUnits = originalUnits.Count > 1 ? string.Join(" + ", originalUnits) : null
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
                    Unit = item.Unit ?? "",
                    Category = item.Category,
                    SourceRecipes = new List<string> { item.Recipe?.Name ?? "Manual" }
                });
            }
        }
    }

    return results;
}

private string NormalizeIngredientName(string name)
{
    // Remove descriptors, trim, lowercase
    return name
        .ToLowerInvariant()
        .Replace("fresh", "")
        .Replace("organic", "")
        .Replace("chopped", "")
        .Replace("diced", "")
        .Replace("minced", "")
        .Trim();
}
```

### Pattern 2: Unit Conversion Table

**What:** Lightweight conversion service using lookup tables for cooking measurements. Supports common unit families (volume, weight, count).

**When to use:** During consolidation and when user manually edits quantities.

**Example:**
```csharp
// UnitConverter.cs
public class UnitConverter
{
    // Conversion factors to base unit (cups for volume, grams for weight)
    private static readonly Dictionary<string, (string Family, decimal ToBase)> ConversionTable = new()
    {
        // Volume family (base: cup)
        { "cup", ("volume", 1m) },
        { "cups", ("volume", 1m) },
        { "c", ("volume", 1m) },
        { "tbsp", ("volume", 1m/16m) },
        { "tablespoon", ("volume", 1m/16m) },
        { "tablespoons", ("volume", 1m/16m) },
        { "tsp", ("volume", 1m/48m) },
        { "teaspoon", ("volume", 1m/48m) },
        { "teaspoons", ("volume", 1m/48m) },
        { "fl oz", ("volume", 1m/8m) },
        { "fluid ounce", ("volume", 1m/8m) },
        { "fluid ounces", ("volume", 1m/8m) },
        { "ml", ("volume", 1m/236.588m) },
        { "milliliter", ("volume", 1m/236.588m) },
        { "milliliters", ("volume", 1m/236.588m) },
        { "l", ("volume", 1000m/236.588m) },
        { "liter", ("volume", 1000m/236.588m) },
        { "liters", ("volume", 1000m/236.588m) },

        // Weight family (base: gram)
        { "g", ("weight", 1m) },
        { "gram", ("weight", 1m) },
        { "grams", ("weight", 1m) },
        { "kg", ("weight", 1000m) },
        { "kilogram", ("weight", 1000m) },
        { "kilograms", ("weight", 1000m) },
        { "oz", ("weight", 28.3495m) },
        { "ounce", ("weight", 28.3495m) },
        { "ounces", ("weight", 28.3495m) },
        { "lb", ("weight", 453.592m) },
        { "lbs", ("weight", 453.592m) },
        { "pound", ("weight", 453.592m) },
        { "pounds", ("weight", 453.592m) },

        // Count family (no conversion)
        { "piece", ("count", 1m) },
        { "pieces", ("count", 1m) },
        { "clove", ("count", 1m) },
        { "cloves", ("count", 1m) },
        { "can", ("count", 1m) },
        { "cans", ("count", 1m) },
        { "bunch", ("count", 1m) },
    };

    public decimal Convert(decimal quantity, string? fromUnit, string toUnit)
    {
        if (string.IsNullOrWhiteSpace(fromUnit) || string.IsNullOrWhiteSpace(toUnit))
            return quantity;

        var from = NormalizeUnit(fromUnit);
        var to = NormalizeUnit(toUnit);

        if (from == to)
            return quantity;

        if (!ConversionTable.TryGetValue(from, out var fromInfo) ||
            !ConversionTable.TryGetValue(to, out var toInfo))
            throw new InvalidOperationException($"Cannot convert {from} to {to}");

        if (fromInfo.Family != toInfo.Family)
            throw new InvalidOperationException($"Cannot convert {fromInfo.Family} to {toInfo.Family}");

        // Convert to base unit, then to target unit
        var baseQuantity = quantity * fromInfo.ToBase;
        return baseQuantity / toInfo.ToBase;
    }

    public string? FindCommonUnit(List<string?> units)
    {
        var normalized = units
            .Where(u => !string.IsNullOrWhiteSpace(u))
            .Select(u => NormalizeUnit(u!))
            .Distinct()
            .ToList();

        if (normalized.Count == 0)
            return null;

        // Check if all units are in same family
        var families = normalized
            .Where(u => ConversionTable.ContainsKey(u))
            .Select(u => ConversionTable[u].Family)
            .Distinct()
            .ToList();

        if (families.Count != 1)
            return null; // Mixed families, can't consolidate

        // Prefer the most common unit in the list
        var mostCommon = normalized
            .GroupBy(u => u)
            .OrderByDescending(g => g.Count())
            .First()
            .Key;

        return mostCommon;
    }

    private string NormalizeUnit(string unit)
    {
        return unit.ToLowerInvariant().Trim();
    }
}
```

### Pattern 3: Drag-Drop List Reordering with blazor-dragdrop

**What:** Use Dropzone component to enable reordering items within categories and moving items between categories.

**When to use:** Shopping list view for manual reordering.

**Example:**
```razor
@* ShoppingListView.razor *@
@using Plk.Blazor.DragDrop

<div class="shopping-list-container">
    @foreach (var category in Categories)
    {
        <CategorySection Category="@category"
                         Items="@GetItemsForCategory(category)"
                         OnReorder="HandleReorder"
                         OnItemChecked="HandleItemChecked"
                         OnItemDeleted="HandleItemDeleted" />
    }
</div>

@* CategorySection.razor *@
<MudPaper Class="mb-4" Elevation="2">
    <div class="category-header" @onclick="ToggleExpanded">
        <MudText Typo="Typo.h6">@Category</MudText>
        <MudChip Size="Size.Small">@Items.Count(i => !i.IsChecked)</MudChip>
    </div>

    @if (_expanded)
    {
        <Dropzone Items="Items"
                  AllowsDrag="AllowsDrag"
                  InstantReplace="true"
                  OnItemDrop="HandleItemDrop">
            <ShoppingListItem Item="@context"
                              OnChecked="() => OnItemChecked.InvokeAsync(context)"
                              OnDeleted="() => OnItemDeleted.InvokeAsync(context)" />
        </Dropzone>
    }
</MudPaper>

@code {
    [Parameter] public string Category { get; set; } = default!;
    [Parameter] public List<ShoppingListItem> Items { get; set; } = new();
    [Parameter] public EventCallback<(ShoppingListItem item, int newIndex)> OnReorder { get; set; }
    [Parameter] public EventCallback<ShoppingListItem> OnItemChecked { get; set; }
    [Parameter] public EventCallback<ShoppingListItem> OnItemDeleted { get; set; }

    private bool _expanded = true;

    private void ToggleExpanded() => _expanded = !_expanded;

    private bool AllowsDrag(ShoppingListItem item) => !item.IsChecked;

    private async Task HandleItemDrop()
    {
        // blazor-dragdrop modifies Items list in place
        // Update SortOrder for all items in category
        for (int i = 0; i < Items.Count; i++)
        {
            Items[i].SortOrder = i;
        }
        await OnReorder.InvokeAsync((Items.First(), 0)); // Signal update
    }
}
```

### Pattern 4: Snackbar Undo for Check-Off Actions

**What:** Show snackbar with undo button when item is checked. Allows tap-to-toggle AND snackbar undo for flexibility.

**When to use:** When user checks off an item.

**Example:**
```csharp
// ShoppingList.razor
@inject ISnackbar Snackbar

private ShoppingListItem? _lastCheckedItem;

private async Task HandleItemChecked(ShoppingListItem item)
{
    var wasChecked = item.IsChecked;
    item.IsChecked = !wasChecked;
    item.CheckedAt = item.IsChecked ? DateTime.UtcNow : null;

    await _shoppingListService.UpdateItemAsync(item);

    if (item.IsChecked)
    {
        _lastCheckedItem = item;

        Snackbar.Add($"Checked: {item.Name}", Severity.Normal, config =>
        {
            config.Action = "UNDO";
            config.ActionColor = Color.Primary;
            config.VisibleStateDuration = 4000;
            config.OnClick = async snackbar =>
            {
                if (_lastCheckedItem != null)
                {
                    await UndoCheckOff(_lastCheckedItem);
                }
            };
        });
    }

    StateHasChanged();
}

private async Task UndoCheckOff(ShoppingListItem item)
{
    item.IsChecked = false;
    item.CheckedAt = null;
    await _shoppingListService.UpdateItemAsync(item);
    StateHasChanged();
}
```

### Pattern 5: FAB with Quick Add Dialog

**What:** Floating action button in bottom-right corner opens quick dialog for adding manual items with autocomplete.

**When to use:** Shopping list page for adding non-recipe items.

**Example:**
```razor
@* ShoppingList.razor *@
<div class="page-container">
    <ShoppingListView ... />

    <!-- Floating Action Button -->
    <MudFab Color="Color.Primary"
            StartIcon="@Icons.Material.Filled.Add"
            Style="position: fixed; bottom: 20px; right: 20px; z-index: 1000;"
            OnClick="OpenAddItemDialog"
            aria-label="Add item" />
</div>

@code {
    private async Task OpenAddItemDialog()
    {
        var parameters = new DialogParameters<AddItemDialog>
        {
            { x => x.HouseholdId, _householdId },
            { x => x.ShoppingListId, _currentListId }
        };

        var options = new DialogOptions
        {
            CloseOnEscapeKey = true,
            BackdropClick = false,
            MaxWidth = MaxWidth.Small,
            FullWidth = true
        };

        var dialog = await DialogService.ShowAsync<AddItemDialog>("Add Item", parameters, options);
        var result = await dialog.Result;

        if (!result.Canceled)
        {
            await LoadShoppingList();
            StateHasChanged();
        }
    }
}

@* AddItemDialog.razor *@
<MudDialog>
    <DialogContent>
        <MudAutocomplete T="string"
                         @bind-Value="_itemName"
                         Label="Item name"
                         SearchFunc="SearchItemHistory"
                         DebounceInterval="300"
                         MinCharacters="2"
                         Variant="Variant.Outlined"
                         FullWidth="true"
                         AutoFocus="true" />

        <MudGrid Class="mt-2">
            <MudItem xs="6">
                <MudNumericField @bind-Value="_quantity"
                                 Label="Quantity (optional)"
                                 Variant="Variant.Outlined" />
            </MudItem>
            <MudItem xs="6">
                <MudAutocomplete T="string"
                                 @bind-Value="_unit"
                                 Label="Unit"
                                 SearchFunc="SearchUnits"
                                 Variant="Variant.Outlined" />
            </MudItem>
        </MudGrid>

        <MudSelect T="string"
                   @bind-Value="_category"
                   Label="Category"
                   Variant="Variant.Outlined"
                   Class="mt-2">
            @foreach (var cat in Categories)
            {
                <MudSelectItem Value="@cat">@cat</MudSelectItem>
            }
        </MudSelect>
    </DialogContent>

    <DialogActions>
        <MudButton OnClick="Cancel">Cancel</MudButton>
        <MudButton Color="Color.Primary"
                   OnClick="Submit"
                   Disabled="@string.IsNullOrWhiteSpace(_itemName)">
            Add
        </MudButton>
    </DialogActions>
</MudDialog>

@code {
    [CascadingParameter] MudDialogInstance MudDialog { get; set; } = default!;
    [Parameter] public int HouseholdId { get; set; }
    [Parameter] public int ShoppingListId { get; set; }

    private string _itemName = string.Empty;
    private decimal? _quantity;
    private string? _unit;
    private string _category = "Pantry";

    private static readonly string[] Categories =
        { "Meat", "Produce", "Dairy", "Pantry", "Spices", "Bakery", "Frozen" };

    private async Task<IEnumerable<string>> SearchItemHistory(string value, CancellationToken token)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length < 2)
            return Enumerable.Empty<string>();

        // Query past shopping list items for autocomplete
        return await _shoppingListService.GetItemNameSuggestionsAsync(
            HouseholdId, value, token);
    }

    private Task<IEnumerable<string>> SearchUnits(string value, CancellationToken token)
    {
        var units = new[] { "cup", "tbsp", "tsp", "oz", "lb", "g", "kg", "piece", "can", "bunch" };
        if (string.IsNullOrWhiteSpace(value))
            return Task.FromResult(units.AsEnumerable());

        return Task.FromResult(units.Where(u =>
            u.StartsWith(value, StringComparison.OrdinalIgnoreCase)).AsEnumerable());
    }

    private async Task Submit()
    {
        var item = new ShoppingListItem
        {
            HouseholdId = HouseholdId,
            ShoppingListId = ShoppingListId,
            Name = _itemName.Trim(),
            Quantity = _quantity,
            Unit = _unit,
            Category = _category,
            AddedAt = DateTime.UtcNow,
            IsChecked = false
        };

        await _shoppingListService.AddManualItemAsync(item);
        MudDialog.Close(DialogResult.Ok(item));
    }

    private void Cancel() => MudDialog.Cancel();
}
```

### Pattern 6: Frequency-Based Initial Ordering

**What:** Track how often items are purchased and use that to order items within categories (most frequent first).

**When to use:** Initial ordering when generating shopping list, before user manually reorders.

**Example:**
```csharp
// ShoppingListService.cs
public class ItemFrequencyScore
{
    public string Name { get; set; } = string.Empty;
    public int PurchaseCount { get; set; }
    public DateTime? LastPurchasedAt { get; set; }

    public decimal Score => CalculateScore();

    private decimal CalculateScore()
    {
        // Simple scoring: purchase count weighted by recency
        var baseScore = PurchaseCount * 10;

        if (LastPurchasedAt.HasValue)
        {
            var daysSinceLastPurchase = (DateTime.UtcNow - LastPurchasedAt.Value).TotalDays;
            var recencyBonus = Math.Max(0, 100 - daysSinceLastPurchase); // Decays over 100 days
            return baseScore + (decimal)recencyBonus;
        }

        return baseScore;
    }
}

public async Task<List<ShoppingListItem>> GetItemsOrderedByFrequencyAsync(
    int householdId,
    int shoppingListId,
    CancellationToken ct = default)
{
    await using var context = await _dbFactory.CreateDbContextAsync(ct);

    var items = await context.ShoppingListItems
        .Where(i => i.HouseholdId == householdId && i.ShoppingListId == shoppingListId)
        .ToListAsync(ct);

    // Calculate frequency scores from historical data
    var scores = await CalculateFrequencyScoresAsync(householdId, items.Select(i => i.Name).ToList(), ct);

    // Order by: category, then frequency score (high to low), then name
    return items
        .OrderBy(i => GetCategoryOrder(i.Category))
        .ThenByDescending(i => scores.GetValueOrDefault(i.Name.ToLower(), 0))
        .ThenBy(i => i.Name)
        .ToList();
}

private async Task<Dictionary<string, decimal>> CalculateFrequencyScoresAsync(
    int householdId,
    List<string> itemNames,
    CancellationToken ct)
{
    await using var context = await _dbFactory.CreateDbContextAsync(ct);

    // Get all historical purchases (checked items from archived lists)
    var history = await context.ShoppingListItems
        .Where(i => i.HouseholdId == householdId
                 && i.IsChecked
                 && i.ShoppingList.IsArchived)
        .GroupBy(i => i.Name.ToLower())
        .Select(g => new ItemFrequencyScore
        {
            Name = g.Key,
            PurchaseCount = g.Count(),
            LastPurchasedAt = g.Max(i => i.CheckedAt)
        })
        .ToListAsync(ct);

    return history.ToDictionary(h => h.Name, h => h.Score);
}

private int GetCategoryOrder(string category)
{
    // Store layout preset - can be customized per household
    var order = new Dictionary<string, int>
    {
        { "Produce", 1 },
        { "Bakery", 2 },
        { "Meat", 3 },
        { "Dairy", 4 },
        { "Frozen", 5 },
        { "Pantry", 6 },
        { "Spices", 7 }
    };

    return order.GetValueOrDefault(category, 99);
}
```

### Anti-Patterns to Avoid

- **Converting all units automatically without user preference:** Some users want "1 bunch cilantro" + "handful cilantro" separate. Respect consolidation toggle setting.
- **Deleting checked items immediately:** Keep them in list (grayed out at bottom) for reference. Only clear via explicit "Clear completed" action.
- **Re-generating list overwrites manual edits:** Track manual adjustments (added items, edited quantities) and preserve them during re-consolidation.
- **Drag-drop on mobile without large touch targets:** Item rows should be min 48px tall for easy tapping and dragging.
- **Using recipe ingredient names verbatim:** "boneless skinless chicken breast" vs "chicken breast" won't consolidate. Normalize names.

## Don't Hand-Roll

Problems that look simple but have existing solutions:

| Problem | Don't Build | Use Instead | Why |
|---------|-------------|-------------|-----|
| Fraction addition | Custom decimal math | Fractions library | "1/2 + 3/4" requires fraction arithmetic, not decimal. Library handles edge cases. |
| Drag-drop UI | Custom JavaScript | blazor-dragdrop | Already installed, handles touch events, mobile-friendly, Blazor-native API. |
| Autocomplete with debounce | Manual event throttling | MudAutocomplete with DebounceInterval | Built-in debouncing, handles async search, keyboard navigation, mobile support. |
| Snackbar queue management | Custom toast component | MudSnackbar with ISnackbar service | Handles multiple snackbars, positioning, auto-dismiss, action buttons. |
| Unit normalization lookup | Regular expressions | Dictionary table | Cooking units are finite (~30 variants). Table is faster and more maintainable. |
| Complex fuzzy matching | Custom Levenshtein | FuzzySharp (if needed) | Optimized C# implementation. But start with simple normalization first. |

**Key insight:** This phase combines simple algorithms (string normalization, lookup tables) with existing Blazor patterns (autocomplete, drag-drop, snackbars). Avoid over-engineering consolidation logic—most ingredients consolidate via exact name matching after normalization.

## Common Pitfalls

### Pitfall 1: Over-Aggressive Consolidation Confuses Users

**What goes wrong:** "chicken breast" + "chicken thighs" + "chicken stock" all merge into one "chicken" entry with incomprehensible quantity.

**Why it happens:** Matching on substring or stemming ("chicken" appears in all) without category boundaries.

**How to avoid:**
1. Use category as consolidation boundary (stock is Pantry, breast/thighs are Meat)
2. Only consolidate on normalized EXACT name match ("chicken breast" == "chicken breast")
3. Don't consolidate if units are incompatible (volume + weight)
4. Show source recipes in UI so users understand why quantities are summed

```csharp
// Good: respects category boundaries
if (item1.NormalizedName == item2.NormalizedName &&
    item1.Category == item2.Category &&
    CanConvertUnits(item1.Unit, item2.Unit))
{
    // Consolidate
}

// Bad: ignores category, uses substring
if (item1.Name.Contains("chicken") && item2.Name.Contains("chicken"))
{
    // Will incorrectly merge stock with meat
}
```

**Warning signs:** User complaints that items "don't make sense", confusion about quantities, manual splitting of consolidated items.

### Pitfall 2: Re-Generation Loses Manual Edits

**What goes wrong:** User adds "Paper towels" manually, edits "2 lbs chicken" to "3 lbs chicken", then re-generates list from updated meal plan. Manual additions and edits disappear.

**Why it happens:** Generator creates fresh list, doesn't preserve existing items.

**How to avoid:**
1. Track item source (RecipeId vs manual flag)
2. Track quantity delta (original: 2 lbs, user edit: +1 lb, delta: +1 lb)
3. During re-generation, preserve manual items and apply deltas

```csharp
public async Task RegenerateShoppingListAsync(int householdId, int shoppingListId, int mealPlanId)
{
    // Load existing list
    var existingItems = await GetShoppingListItemsAsync(householdId, shoppingListId);

    // Separate manual vs generated items
    var manualItems = existingItems.Where(i => i.RecipeIngredientId == null).ToList();
    var editedItems = existingItems.Where(i => i.QuantityDelta.HasValue).ToDictionary(i => i.Name);

    // Generate fresh from meal plan
    var newItems = await _generator.GenerateFromMealPlanAsync(householdId, mealPlanId);

    // Apply deltas to matching items
    foreach (var item in newItems)
    {
        if (editedItems.TryGetValue(item.Name, out var edited))
        {
            item.Quantity += edited.QuantityDelta ?? 0;
        }
    }

    // Merge manual items back in
    newItems.AddRange(manualItems);

    // Save updated list
    await ReplaceShoppingListItemsAsync(householdId, shoppingListId, newItems);
}
```

**Warning signs:** User says "I added items but they disappeared", support tickets about lost edits.

### Pitfall 3: Category Reordering Not Persisted

**What goes wrong:** User drags Produce category to top (their store layout), but on next visit categories reset to default order.

**Why it happens:** Category order is hardcoded in UI, not stored in database.

**How to avoid:**
1. Add `CategoryOrder` table with household-specific ordering
2. Or add `CategorySortOrder` column to ShoppingList entity (JSON array of category names in order)
3. Load user's preferred order on page load

```csharp
// StoreLayoutPreset entity (simpler approach)
public class StoreLayoutPreset
{
    public int HouseholdId { get; set; }
    public string Name { get; set; } = "Default"; // "Kroger", "Walmart", "Custom"
    public string[] CategoryOrder { get; set; } = Array.Empty<string>();
}

// Service method
public async Task<string[]> GetCategoryOrderAsync(int householdId)
{
    var preset = await _context.StoreLayoutPresets
        .FirstOrDefaultAsync(p => p.HouseholdId == householdId && p.IsActive);

    return preset?.CategoryOrder ?? DefaultCategoryOrder;
}
```

**Warning signs:** Users manually reorder categories every visit, requests for "save my layout".

### Pitfall 4: Drag-Drop Breaks on Mobile Touch

**What goes wrong:** Drag-drop works on desktop but fails on mobile touch devices.

**Why it happens:** blazor-dragdrop uses mouse events, not touch events, OR touch target too small.

**How to avoid:**
1. Verify blazor-dragdrop 2.6.1 supports touch (it does based on docs)
2. Ensure item rows have min 48px height for touch target
3. Test on actual mobile device, not just browser DevTools
4. Consider drag handle icon (≡) for explicit drag affordance

```razor
<div class="shopping-list-item" style="min-height: 48px; padding: 12px;">
    <MudIcon Icon="@Icons.Material.Filled.DragIndicator"
             Class="drag-handle"
             Size="Size.Small" />
    <MudCheckbox @bind-Checked="Item.IsChecked" />
    <MudText>@Item.Name</MudText>
</div>

<style>
    .drag-handle {
        cursor: grab;
        touch-action: none; /* Prevent scroll during drag */
    }
</style>
```

**Warning signs:** Mobile users report "can't reorder items", drag doesn't start on touch.

### Pitfall 5: Snackbar Undo Executes Wrong Item

**What goes wrong:** User checks off multiple items quickly, clicks undo, wrong item gets unchecked.

**Why it happens:** `_lastCheckedItem` reference gets overwritten before user clicks undo. Race condition.

**How to avoid:**
1. Capture item reference in lambda closure, not shared field
2. Or pass item ID in snackbar data

```csharp
// Bad: shared reference
private ShoppingListItem? _lastCheckedItem;

private void HandleCheck(ShoppingListItem item)
{
    _lastCheckedItem = item; // Gets overwritten if multiple checks
    Snackbar.Add("Checked", config =>
    {
        config.OnClick = () => UndoCheck(_lastCheckedItem); // Wrong item!
    });
}

// Good: closure captures item
private void HandleCheck(ShoppingListItem item)
{
    var capturedItem = item; // Local variable captured in closure
    item.IsChecked = true;

    Snackbar.Add($"Checked: {item.Name}", Severity.Normal, config =>
    {
        config.Action = "UNDO";
        config.OnClick = async snackbar =>
        {
            await UndoCheck(capturedItem); // Correct item!
        };
    });
}
```

**Warning signs:** Undo button unchecks different item than expected, user confusion.

### Pitfall 6: Autocomplete Doesn't Show Recent Items

**What goes wrong:** User previously bought "Paper towels" but autocomplete doesn't suggest it when typing "pap".

**Why it happens:** Query uses `StartsWith` instead of `Contains`, or doesn't search archived lists.

**How to avoid:**
1. Search ALL lists (active + archived) for item name suggestions
2. Use `Contains` for partial match, OR dual query (StartsWith first, Contains fallback)
3. Rank by frequency (purchased count)

```csharp
public async Task<List<string>> GetItemNameSuggestionsAsync(
    int householdId,
    string prefix,
    CancellationToken ct = default)
{
    await using var context = await _dbFactory.CreateDbContextAsync(ct);

    var lower = prefix.ToLowerInvariant();

    // Prioritize StartsWith matches, then Contains matches
    var startsWith = await context.ShoppingListItems
        .Where(i => i.HouseholdId == householdId
                 && i.Name.ToLower().StartsWith(lower))
        .GroupBy(i => i.Name)
        .Select(g => new { Name = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .Take(10)
        .Select(x => x.Name)
        .ToListAsync(ct);

    if (startsWith.Count >= 5)
        return startsWith;

    var contains = await context.ShoppingListItems
        .Where(i => i.HouseholdId == householdId
                 && i.Name.ToLower().Contains(lower)
                 && !i.Name.ToLower().StartsWith(lower))
        .GroupBy(i => i.Name)
        .Select(g => new { Name = g.Key, Count = g.Count() })
        .OrderByDescending(x => x.Count)
        .Take(10 - startsWith.Count)
        .Select(x => x.Name)
        .ToListAsync(ct);

    return startsWith.Concat(contains).ToList();
}
```

**Warning signs:** Users manually type full item names instead of using autocomplete, duplicate items with slight spelling variations.

## Code Examples

Verified patterns from MudBlazor documentation and existing codebase:

### ShoppingListService Implementation

```csharp
// ShoppingListService.cs
public interface IShoppingListService
{
    Task<ShoppingList> CreateShoppingListAsync(int householdId, string name, int? mealPlanId = null, CancellationToken ct = default);
    Task<ShoppingList?> GetShoppingListAsync(int householdId, int shoppingListId, CancellationToken ct = default);
    Task<List<ShoppingList>> GetActiveShoppingListsAsync(int householdId, CancellationToken ct = default);
    Task<ShoppingListItem> AddManualItemAsync(ShoppingListItem item, CancellationToken ct = default);
    Task UpdateItemAsync(ShoppingListItem item, CancellationToken ct = default);
    Task DeleteItemAsync(int householdId, int shoppingListId, int itemId, CancellationToken ct = default);
    Task<List<string>> GetItemNameSuggestionsAsync(int householdId, string prefix, CancellationToken ct = default);
}

public class ShoppingListService : IShoppingListService
{
    private readonly IDbContextFactory<ApplicationDbContext> _dbFactory;
    private readonly ILogger<ShoppingListService> _logger;

    public ShoppingListService(
        IDbContextFactory<ApplicationDbContext> dbFactory,
        ILogger<ShoppingListService> logger)
    {
        _dbFactory = dbFactory;
        _logger = logger;
    }

    public async Task<ShoppingList> CreateShoppingListAsync(
        int householdId,
        string name,
        int? mealPlanId = null,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

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

        _logger.LogInformation(
            "Created ShoppingList {ShoppingListId} for household {HouseholdId}",
            shoppingList.ShoppingListId, householdId);

        return shoppingList;
    }

    public async Task<ShoppingList?> GetShoppingListAsync(
        int householdId,
        int shoppingListId,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        return await context.ShoppingLists
            .Include(sl => sl.Items)
            .Include(sl => sl.MealPlan)
            .FirstOrDefaultAsync(
                sl => sl.HouseholdId == householdId && sl.ShoppingListId == shoppingListId,
                ct);
    }

    public async Task<ShoppingListItem> AddManualItemAsync(
        ShoppingListItem item,
        CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var maxItemId = await context.ShoppingListItems
            .Where(i => i.HouseholdId == item.HouseholdId
                     && i.ShoppingListId == item.ShoppingListId)
            .MaxAsync(i => (int?)i.ItemId, ct) ?? 0;

        item.ItemId = maxItemId + 1;
        item.AddedAt = DateTime.UtcNow;

        context.ShoppingListItems.Add(item);
        await context.SaveChangesAsync(ct);

        _logger.LogInformation(
            "Added manual item {ItemId} to ShoppingList {ShoppingListId}",
            item.ItemId, item.ShoppingListId);

        return item;
    }

    public async Task UpdateItemAsync(ShoppingListItem item, CancellationToken ct = default)
    {
        await using var context = await _dbFactory.CreateDbContextAsync(ct);

        var existing = await context.ShoppingListItems
            .FirstOrDefaultAsync(
                i => i.HouseholdId == item.HouseholdId
                  && i.ShoppingListId == item.ShoppingListId
                  && i.ItemId == item.ItemId,
                ct);

        if (existing == null)
            throw new InvalidOperationException($"Item {item.ItemId} not found");

        existing.Name = item.Name;
        existing.Quantity = item.Quantity;
        existing.Unit = item.Unit;
        existing.Category = item.Category;
        existing.IsChecked = item.IsChecked;
        existing.CheckedAt = item.CheckedAt;

        await context.SaveChangesAsync(ct);
    }
}
```

### ShoppingListItem Component with Tap-to-Check

```razor
@* ShoppingListItem.razor *@
<div class="shopping-list-item @GetItemClass()" @onclick="HandleClick">
    <MudIcon Icon="@Icons.Material.Filled.DragIndicator"
             Class="drag-handle"
             Size="Size.Small" />

    <MudCheckbox Checked="@Item.IsChecked"
                 CheckedChanged="HandleCheckChanged"
                 Class="item-checkbox"
                 @onclick:stopPropagation="true" />

    <div class="item-content">
        <MudText Typo="Typo.body1" Class="@GetTextClass()">
            @if (Item.Quantity.HasValue)
            {
                <span class="quantity">@FormatQuantity(Item.Quantity.Value) @Item.Unit</span>
            }
            <span class="name">@Item.Name</span>
        </MudText>

        @if (!string.IsNullOrEmpty(Item.SourceRecipes))
        {
            <MudText Typo="Typo.caption" Color="Color.Secondary">
                From: @Item.SourceRecipes
            </MudText>
        }
    </div>

    <div class="item-actions">
        <MudIconButton Icon="@Icons.Material.Filled.Edit"
                       Size="Size.Small"
                       OnClick="HandleEdit"
                       @onclick:stopPropagation="true" />
        <MudIconButton Icon="@Icons.Material.Filled.Delete"
                       Size="Size.Small"
                       Color="Color.Error"
                       OnClick="HandleDelete"
                       @onclick:stopPropagation="true" />
    </div>
</div>

@code {
    [Parameter] public ShoppingListItem Item { get; set; } = default!;
    [Parameter] public EventCallback OnChecked { get; set; }
    [Parameter] public EventCallback OnEdited { get; set; }
    [Parameter] public EventCallback OnDeleted { get; set; }

    private string GetItemClass()
    {
        return Item.IsChecked ? "item-checked" : "item-unchecked";
    }

    private string GetTextClass()
    {
        return Item.IsChecked ? "text-decoration-line-through text-muted" : "";
    }

    private async Task HandleClick()
    {
        // Tap anywhere on row to check/uncheck
        await HandleCheckChanged(!Item.IsChecked);
    }

    private async Task HandleCheckChanged(bool isChecked)
    {
        Item.IsChecked = isChecked;
        await OnChecked.InvokeAsync();
    }

    private async Task HandleEdit() => await OnEdited.InvokeAsync();
    private async Task HandleDelete() => await OnDeleted.InvokeAsync();

    private string FormatQuantity(decimal quantity)
    {
        // Use Fractions library for display
        var fraction = (Fraction)quantity;
        return fraction.ToString(); // Shows "1/2" instead of "0.5"
    }
}

<style>
    .shopping-list-item {
        display: flex;
        align-items: center;
        gap: 12px;
        padding: 12px;
        min-height: 48px;
        border-bottom: 1px solid var(--mud-palette-lines-default);
        cursor: pointer;
        transition: background-color 0.2s;
    }

    .shopping-list-item:hover {
        background-color: var(--mud-palette-action-default-hover);
    }

    .drag-handle {
        cursor: grab;
        color: var(--mud-palette-text-secondary);
    }

    .item-content {
        flex: 1;
        min-width: 0; /* Allow text truncation */
    }

    .item-actions {
        display: flex;
        gap: 4px;
        opacity: 0;
        transition: opacity 0.2s;
    }

    .shopping-list-item:hover .item-actions {
        opacity: 1;
    }

    .item-checked {
        opacity: 0.6;
    }

    .quantity {
        font-weight: 600;
        margin-right: 8px;
    }
</style>
```

### UnitConverter Service Usage

```csharp
// Example: Consolidating ingredients with unit conversion
var ingredients = new List<RecipeIngredient>
{
    new() { Name = "Milk", Quantity = 1, Unit = "cup", Category = "Dairy" },
    new() { Name = "Milk", Quantity = 8, Unit = "fl oz", Category = "Dairy" },
    new() { Name = "Milk", Quantity = 118, Unit = "ml", Category = "Dairy" }
};

var converter = new UnitConverter();

// Find common unit (all are volume)
var commonUnit = converter.FindCommonUnit(ingredients.Select(i => i.Unit).ToList());
// Returns "cup" (most common)

// Convert all to cups
decimal totalCups = 0;
var originalUnits = new List<string>();

foreach (var ing in ingredients)
{
    var converted = converter.Convert(ing.Quantity ?? 0, ing.Unit, "cup");
    totalCups += converted;
    originalUnits.Add($"{ing.Quantity} {ing.Unit}");
}

// totalCups = 2.5 (1 cup + 1 cup + 0.5 cup)
// originalUnits = ["1 cup", "8 fl oz", "118 ml"]

var consolidatedItem = new ShoppingListItem
{
    Name = "Milk",
    Quantity = totalCups,
    Unit = "cup",
    Category = "Dairy",
    SourceRecipes = "Pancakes, Smoothie, Coffee", // From recipe tracking
    OriginalUnits = "1 cup + 8 fl oz + 118 ml" // For user reference
};
```

## State of the Art

| Old Approach | Current Approach | When Changed | Impact |
|--------------|------------------|--------------|--------|
| Manual copy-paste from recipes | Auto-generate from meal plan | 2020+ meal planning apps | Eliminates manual list creation, reduces errors |
| Duplicate ingredients as separate rows | Smart consolidation with unit conversion | 2022+ (Plan to Eat, Mealime) | Cleaner lists, less shopping confusion |
| Fixed store layout | User-customizable category order | 2023+ | Adapts to different grocery stores |
| Check-off items disappear | Checked items stay grayed at bottom | 2024+ | User can verify they got everything |
| Separate mobile app | Responsive web with offline support | 2025+ PWA | Single codebase, works offline |
| Basic text autocomplete | Frequency-based suggestions | 2026 AI features | Learns user shopping patterns |

**Deprecated/outdated:**
- **Deleting checked items:** Modern UX keeps them visible (grayed) for trip verification
- **Fixed category order:** Users shop at different stores with different layouts
- **No consolidation:** Seeing "1 cup milk" and "8 oz milk" as separate items is confusing
- **Regenerating list loses manual additions:** Users add non-recipe items (paper towels, dog food)

## Open Questions

Things that couldn't be fully resolved:

1. **Fuzzy Ingredient Matching Necessity**
   - What we know: FuzzySharp exists for fuzzy string matching, PlanToEat uses normalized exact matching
   - What's unclear: Will users enter ingredients inconsistently enough to need fuzzy matching ("chicken breast" vs "chicken brst" typo)?
   - Recommendation: Start with normalized exact matching (trim, lowercase, remove descriptors). Add FuzzySharp if user testing shows high false negatives (items that should consolidate but don't). Mark as LOW confidence until validated with real data.

2. **Store Layout Preset Details**
   - What we know: Kroger and Walmart have different category orderings, users want customization
   - What's unclear: Exact category sequences for major chains, whether to provide presets or just default + custom
   - Recommendation: Start with single default layout (Produce → Bakery → Meat → Dairy → Frozen → Pantry → Spices). Add preset selector in Phase 5+ if users request it. Custom reordering via drag-drop is sufficient for MVP.

3. **Handling Quantity-less Ingredients**
   - What we know: Some ingredients have no quantity ("salt to taste", "1 bunch cilantro")
   - What's unclear: Should these auto-consolidate? Display as "2x 1 bunch cilantro" or keep separate?
   - Recommendation: Keep separate if no quantity or imprecise unit. Better to have two "1 bunch" entries than confusing "2 bunches" when user wanted specific counts. User can manually delete duplicate if desired.

4. **Multi-List Active Workflows**
   - What we know: Users want multiple named lists ("Weekly groceries", "Costco run")
   - What's unclear: Should there be one "active" list or multiple active lists? Navigation between lists?
   - Recommendation: Support multiple active (non-archived) lists. Main page shows most recent list. Add list selector dropdown in header. This mirrors meal plan week navigation pattern.

5. **Re-consolidation After Manual Edits**
   - What we know: User can manually edit quantities, meal plan might change
   - What's unclear: When meal plan updates, should list re-consolidate? Preserve manual edits as deltas?
   - Recommendation: Track `QuantityDelta` field (difference between generated and user-edited quantity). During re-generation, apply deltas to new consolidated values. Show warning dialog: "Meal plan changed. Update list? Manual edits will be preserved."

## Sources

### Primary (HIGH confidence)

- MudBlazor Autocomplete: https://mudblazor.com/components/select (verified API for SearchFunc, DebounceInterval)
- MudBlazor Snackbar: https://mudblazor.com/components/snackbar (verified action buttons and OnClick handlers)
- MudBlazor FAB: https://mudblazor.com/components/buttonfab (verified properties, positioning requires CSS)
- blazor-dragdrop GitHub: https://github.com/Postlagerkarte/blazor-dragdrop (verified Dropzone API, InstantReplace)
- Fractions NuGet: https://www.nuget.org/packages/fractions/ (verified fraction arithmetic capabilities)
- Existing codebase patterns: RecipeService, IngredientEntry.razor (autocomplete implementation), ShoppingList entities

### Secondary (MEDIUM confidence)

- Plan to Eat consolidation: https://learn.plantoeat.com/help/manually-merge-ingredients-on-your-shopping-list (verified consolidation requires matching title/unit/category)
- MealFlow recipe-to-list: https://www.mealflow.ai/blog/recipe-to-grocery-list (verified unit conversion challenges in consolidation)
- Walmart store layout: https://www.coohom.com/article/floor-plan-walmart-aisle-layout (category ordering insights)
- UnitsNet GitHub: https://github.com/angularsen/UnitsNet (volume/mass unit support verified, but overkill for this domain)

### Tertiary (LOW confidence - requires validation)

- FuzzySharp for ingredient matching: https://github.com/JakeBayer/FuzzySharp (available but not verified as necessary)
- Frequency scoring algorithms: DataGenetics feed ranking http://datagenetics.com/blog/october32018/index.html (conceptual, not shopping-specific)
- Shopping list AI trends 2026: https://fitia.app/learn/article/7-meal-planning-apps-smart-grocery-lists-us/ (market research, not technical implementation)

## Metadata

**Confidence breakdown:**
- Standard stack (MudBlazor components, blazor-dragdrop, Fractions): HIGH - All packages verified in project, documentation reviewed
- Unit conversion approach (custom table vs UnitsNet): HIGH - Cooking unit domain is well-defined, simple conversions verified
- Consolidation logic (normalization, exact matching): MEDIUM - Pattern verified in existing apps, but semantic matching complexity unknown until real data
- Drag-drop implementation: MEDIUM - Library verified, but mobile touch behavior needs device testing
- Frequency tracking: LOW - Algorithm concept sound, but scoring formula not validated with real usage data
- Store layout presets: LOW - Category ordering verified for Walmart, but comprehensive preset data not found

**Research date:** 2026-01-24
**Valid until:** ~30 days (Blazor/MudBlazor stable, but shopping list UX patterns evolving rapidly in 2026 with AI features)

**Re-research needed if:**
- Consolidation false negatives exceed 20% (fuzzy matching becomes necessary)
- Mobile drag-drop issues on device testing (may need alternative library)
- Users request store-specific layouts (need to research actual store category sequences)
- Performance issues with autocomplete on large datasets (>1000 historical items)
