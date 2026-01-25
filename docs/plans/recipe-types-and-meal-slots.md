# Recipe Types & Multi-Recipe Meal Slots

*Created: 2026-01-25*
*Status: Planning*

## Problem

Recipes are individual dishes (guacamole, tacos, rice), but meals are composed of multiple dishes. Currently:
- One recipe per meal plan slot
- No way to categorize recipes by their role in a meal

## Solution

### Phase 1: Recipe Types

Add a `RecipeType` enum and field to categorize recipes:

```csharp
public enum RecipeType
{
    Main,       // Primary dish (tacos, steak, pasta)
    Side,       // Accompaniment (rice, guac, salad)
    Appetizer,  // Starter (soup, bruschetta)
    Dessert,    // Sweet ending
    Beverage,   // Drinks (margarita, smoothie)
    Sauce,      // Condiments (salsa, dressing)
    Other       // Catch-all
}
```

**Changes needed:**
- [ ] Add `RecipeType` enum
- [ ] Add `RecipeType` field to `Recipe` entity (default: Main)
- [ ] Migration to add column
- [ ] Update RecipeEdit page with type selector
- [ ] Update RecipeCard to show type badge
- [ ] Update recipe import to try to infer type (optional)

### Phase 2: Multi-Recipe Meal Slots

Allow meal plan entries to reference multiple recipes:

**Option A: Junction Table**
```
MealPlanEntry (1) ──── (many) MealPlanRecipe ──── (many) Recipe
```
- Most flexible
- Supports ordering, notes per recipe
- More complex queries

**Option B: Recipe Array on Entry**
```csharp
public class MealPlanEntry
{
    public List<int> RecipeIds { get; set; }  // JSON array in DB
}
```
- Simpler implementation
- Works for PostgreSQL (jsonb)
- Less normalized but pragmatic

**Recommendation:** Option B for simplicity, can migrate to A if needed.

**Changes needed:**
- [ ] Modify `MealPlanEntry` to support multiple recipes
- [ ] Update Meal Plan UI to show/add multiple recipes per slot
- [ ] Update shopping list aggregation to handle multiple recipes
- [ ] Recipe picker dialog allows multi-select

### Phase 3: Meal Composer (Future)

*Deferred — revisit after using multi-recipe slots*

Potential "Meal" entity that groups recipes into reusable combos:
- "Taco Night" = Tacos + Guac + Rice + Margarita
- Can be added to meal plan as a unit
- TBD based on how multi-recipe slots feel in practice

## UI Mockup Ideas

### Recipe Card with Type Badge
```
┌─────────────────────────┐
│ [Image]                 │
│                         │
│ Guacamole        [Side] │
│ Fresh avocado dip...    │
└─────────────────────────┘
```

### Meal Plan Slot with Multiple Recipes
```
┌─────────────────────────────────┐
│ DINNER - Sunday                 │
├─────────────────────────────────┤
│ 🍖 Carne Asada Tacos    [Main]  │
│ 🥑 Guacamole            [Side]  │
│ 🍚 Mexican Rice         [Side]  │
│ 🍹 Margarita         [Beverage] │
│                                 │
│ [+ Add Recipe]                  │
└─────────────────────────────────┘
```

## Implementation Order

1. Recipe types (quick win, useful immediately)
2. Multi-recipe slots (requires more work)
3. Evaluate need for meal composer

---

*Ready to implement when prioritized*
