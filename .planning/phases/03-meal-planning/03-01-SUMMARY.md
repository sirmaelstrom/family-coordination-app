---
phase: 03-meal-planning
plan: "01"
subsystem: backend-services
tags: [meal-planning, service-layer, crud, ef-core]

dependencies:
  requires:
    - "01-01: ApplicationDbContext with MealPlan/MealPlanEntry entities"
    - "01-01: IDbContextFactory pattern established"
  provides:
    - IMealPlanService interface
    - MealPlanService CRUD operations
    - Week-based meal plan access pattern
  affects:
    - "03-02+: UI components can inject IMealPlanService"
    - "04-*: Shopping list generation can query meal plans"

tech-stack:
  added: []
  patterns:
    - Get-or-create pattern for weekly meal plans
    - Composite key operations with household isolation
    - Include navigation for Recipe when loading entries

key-files:
  created:
    - src/FamilyCoordinationApp/Services/MealPlanService.cs
  modified:
    - src/FamilyCoordinationApp/Program.cs

decisions:
  - key: meal-plan-get-or-create
    what: GetOrCreateMealPlanAsync ensures MealPlan exists for any week accessed
    why: Simplifies UI logic - no need to check/create meal plan before adding entries
    impact: Automatic MealPlan creation on first access to any week
  - key: entry-upsert-pattern
    what: AddMealAsync updates if entry exists at date+mealType, creates if not
    why: Prevents duplicate entries for same slot, allows changing meal selection
    impact: Natural "replace meal" behavior without explicit update method
  - key: recipe-xor-custom
    what: MealPlanEntry must have either RecipeId OR CustomMealName, not both
    why: Entry represents single meal source - structured recipe or freeform text
    impact: Validation enforced in service layer

metrics:
  duration: 3.4min
  files_created: 1
  files_modified: 1
  commits: 2
  completed: 2026-01-24
---

# Phase 03 Plan 01: Meal Plan Service Summary

Backend service for weekly meal plan CRUD operations following RecipeService patterns.

## What Was Built

Created `MealPlanService` providing meal plan data access for UI components:

**IMealPlanService Interface:**
- `GetOrCreateMealPlanAsync` - Retrieve or create meal plan for a week
- `AddMealAsync` - Add or update meal entry with recipe or custom name
- `RemoveMealAsync` - Hard delete meal entry
- `GetWeekStartDate` - Calculate Monday for any date
- `GetWeekDays` - Generate 7-day array from week start

**Implementation Details:**
- Uses IDbContextFactory pattern (Blazor Server thread safety requirement)
- Includes Recipe navigation when loading entries
- Enforces composite key operations (HouseholdId, MealPlanId, EntryId)
- Get-or-create pattern eliminates "meal plan not found" errors
- Upsert logic in AddMealAsync (update if exists, create if not)

**Service Registration:**
- Registered as scoped service in Program.cs
- Available for injection in Blazor components

## Technical Architecture

**Service Pattern:**
```
Component → IMealPlanService → ApplicationDbContext → PostgreSQL
                ↓
          (via IDbContextFactory)
```

**Data Flow:**
1. Component requests meal plan for week (e.g., "2026-01-20")
2. Service calculates week start (Monday): `GetWeekStartDate` → 2026-01-19
3. Service queries or creates MealPlan for (HouseholdId, WeekStartDate)
4. Service loads entries with `.Include(mp => mp.Entries).ThenInclude(e => e.Recipe)`
5. Returns populated MealPlan to component

**Key Operations:**

*Get or Create Meal Plan:*
```csharp
var mealPlan = await GetOrCreateMealPlanAsync(householdId, weekStart);
// Always returns valid MealPlan - creates if doesn't exist
```

*Add/Update Meal:*
```csharp
// Check if entry exists at date+mealType, update if yes, create if no
var entry = await AddMealAsync(householdId, date, MealType.Dinner, recipeId: 5, null);
// OR
var entry = await AddMealAsync(householdId, date, MealType.Lunch, null, "Eating out");
```

*Remove Meal:*
```csharp
await RemoveMealAsync(householdId, mealPlanId, entryId);
// Hard delete - entry is permanently removed
```

## Decisions Made

### 1. Get-or-create pattern for MealPlan
**Decision:** `GetOrCreateMealPlanAsync` automatically creates MealPlan if it doesn't exist for the requested week.

**Rationale:**
- Simplifies UI logic - no need for "create meal plan" button or check-then-create logic
- Matches user mental model - "I'm planning for next week" not "I must create a plan entity first"
- MealPlan is metadata (week start date) - lightweight to auto-create

**Impact:**
- UI components can directly call `GetOrCreateMealPlanAsync` without null checks
- First access to any week automatically provisions MealPlan entity
- MealPlan creation is implicit, not explicit user action

### 2. Upsert logic in AddMealAsync
**Decision:** `AddMealAsync` updates existing entry at same date+mealType instead of throwing error.

**Rationale:**
- Natural "change my mind" flow - drag new recipe to slot, replaces old one
- Avoids UI complexity of "remove then add" operations
- Single method handles both create and update cases

**Impact:**
- Component can always call `AddMealAsync` regardless of slot state
- Duplicate entries at same slot prevented by service layer
- UI provides seamless "replace meal" experience

### 3. RecipeId XOR CustomMealName validation
**Decision:** Service validates that exactly one of RecipeId or CustomMealName is set, not both.

**Rationale:**
- Entry represents single meal source - either structured recipe or freeform text
- Prevents ambiguous state ("which should UI display?")
- Enforces data model constraint at service boundary

**Impact:**
- InvalidOperationException thrown if both or neither provided
- Clear contract for component developers
- Data integrity guaranteed before database write

### 4. Hard delete for entries
**Decision:** `RemoveMealAsync` hard deletes entry, no soft delete.

**Rationale:**
- MealPlanEntry is planning data, not historical record
- User expectation: "remove meal" means it's gone
- Soft delete adds complexity (filters, restore UI) without value for MVP

**Impact:**
- Deleted entries are permanently removed
- Simplifies queries (no need for `Where(e => !e.IsDeleted)`)
- No "restore deleted meal" feature needed

### 5. Week calculation with GetWeekStartDate
**Decision:** Week starts on Monday, calculated via `(7 + (date.DayOfWeek - DayOfWeek.Monday)) % 7`.

**Rationale:**
- Standard business week convention (Monday-Sunday)
- Algorithm handles Sunday (DayOfWeek.Sunday = 0) edge case
- Consistent with user expectation for "this week's plan"

**Impact:**
- All meal plans align to Monday-Sunday weeks
- DateOnly calculations handled by service, not UI
- Components work with MealPlan.WeekStartDate as week identifier

## Deviations from Plan

None - plan executed exactly as written.

## Next Phase Readiness

**Blockers:** None

**Concerns:** None

**Recommendations:**
- Next plan (03-02) should implement UI components that consume IMealPlanService
- Consider adding `GetMealPlanAsync` (without create) for read-only scenarios
- Future enhancement: `UpdateMealNotesAsync` for editing entry notes without full replace

## Testing Notes

**Manual Verification:**
- Build passes: `dotnet build src/FamilyCoordinationApp`
- MealPlanService.cs created with 177 lines
- IMealPlanService registered in Program.cs
- Service follows IDbContextFactory pattern from RecipeService

**Integration Points:**
- Service ready for injection into Blazor components
- Compatible with ApplicationDbContext composite key schema
- Works with MealPlan/MealPlanEntry entities from Phase 01

**Not Tested (out of scope for this plan):**
- Actual database operations (requires running app with seeded data)
- Concurrent access scenarios (IDbContextFactory handles thread safety)
- Validation of RecipeId foreign key (database enforces constraint)

## Performance Considerations

**Database Queries:**
- `GetOrCreateMealPlanAsync`: Single query with includes (entries + recipes)
- `AddMealAsync`: Two queries (check existing, insert/update) - acceptable for user-driven action
- `RemoveMealAsync`: Single delete by composite key

**Optimizations Applied:**
- Eager loading with `.Include()` reduces N+1 queries
- Composite key queries use indexes (HouseholdId always first)
- `GetWeekDays` is pure calculation (no database access)

**Future Optimization Opportunities:**
- Cache week start calculations (pure function, predictable inputs)
- Batch entry operations if UI supports multi-drag (not in MVP scope)

## Knowledge Transfer

**For Future Developers:**

*Adding a meal to plan:*
```csharp
@inject IMealPlanService MealPlanService

var entry = await MealPlanService.AddMealAsync(
    householdId: currentHouseholdId,
    date: DateOnly.FromDateTime(DateTime.Today.AddDays(1)),
    mealType: MealType.Dinner,
    recipeId: selectedRecipeId,
    customMealName: null
);
```

*Loading week's meals:*
```csharp
var weekStart = MealPlanService.GetWeekStartDate(DateOnly.FromDateTime(DateTime.Today));
var mealPlan = await MealPlanService.GetOrCreateMealPlanAsync(currentHouseholdId, weekStart);

// mealPlan.Entries contains all meals for the week
// Entries are already filtered by HouseholdId and MealPlanId (EF query)
```

*Week navigation:*
```csharp
var thisWeek = MealPlanService.GetWeekStartDate(DateOnly.FromDateTime(DateTime.Today));
var nextWeek = thisWeek.AddDays(7);
var previousWeek = thisWeek.AddDays(-7);

var days = MealPlanService.GetWeekDays(thisWeek);
// days[0] = Monday, days[6] = Sunday
```

**Common Pitfalls:**
- Don't call `AddMealAsync` with both recipeId and customMealName - throws InvalidOperationException
- Don't assume MealPlan already exists - always use `GetOrCreateMealPlanAsync`
- Don't forget to pass correct MealPlanId to `RemoveMealAsync` - comes from MealPlan entity
- Remember weekStart is always Monday - use `GetWeekStartDate` for consistent calculation

**Extension Points:**
- Add `GetMealsByDateRangeAsync` for calendar view spanning multiple weeks
- Add `CopyWeekAsync` to duplicate meal plan to another week
- Add `UpdateEntryNotesAsync` for editing notes without replacing entire entry

## Related Documentation

- Phase 01-01: Database schema with MealPlan/MealPlanEntry entities
- Phase 02-03: RecipeService pattern (used as template)
- PROJECT.md: Decision to use composite keys with HouseholdId-first ordering
