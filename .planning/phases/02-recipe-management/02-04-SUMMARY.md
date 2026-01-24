---
phase: 02-recipe-management
plan: 04
subsystem: recipe-ui
tags: [blazor, mudblazor, drag-drop, parsing, components]
completed: 2026-01-24
duration: 8 minutes

dependencies:
  requires:
    - "02-01: MudBlazor UI framework and category entities"
    - "02-02: IngredientParser service with tokenization"
    - "02-03: RecipeService and CategoryService"
  provides:
    - "IngredientEntry component with natural language parsing"
    - "BulkPasteDialog component for multi-line ingredient import"
    - "IngredientList component with drag-drop reordering"
    - "Ingredient autocomplete from household history"
  affects:
    - "02-05: Recipe CRUD pages will use these components"
    - "04-01: Shopping list will use same category logic"

tech-stack:
  added:
    - package: "blazor-dragdrop@2.6.1"
      purpose: "Drag-drop ingredient reordering with mobile support"
    - package: "mobile-drag-drop polyfill"
      purpose: "Touch-based drag-drop for mobile devices"
  patterns:
    - "Hybrid ingredient entry: type natural language OR edit structured fields"
    - "Category auto-suggestion based on ingredient name heuristics"
    - "Undo pattern with snackbar action button"
    - "Dialog parameter passing via DialogParameters"

key-files:
  created:
    - src/FamilyCoordinationApp/Services/CategoryService.cs
    - src/FamilyCoordinationApp/Components/Recipe/IngredientEntry.razor
    - src/FamilyCoordinationApp/Components/Recipe/BulkPasteDialog.razor
    - src/FamilyCoordinationApp/Components/Recipe/IngredientList.razor
  modified:
    - src/FamilyCoordinationApp/FamilyCoordinationApp.csproj (added blazor-dragdrop)
    - src/FamilyCoordinationApp/Components/_Imports.razor (added Plk.Blazor.DragDrop using)
    - src/FamilyCoordinationApp/Components/App.razor (added mobile-drag-drop polyfill)
    - src/FamilyCoordinationApp/Program.cs (registered ICategoryService)
    - src/FamilyCoordinationApp/Services/IngredientParser.cs (added IsComplete property)

decisions:
  - id: drag-drop-package
    choice: "blazor-dragdrop over custom JavaScript implementation"
    rationale: "Mature package with mobile support, simpler than building custom drag-drop"
    alternatives: ["Custom JS with Blazor interop", "SortableJS wrapper"]
  - id: category-heuristics
    choice: "Simple keyword-based category suggestion"
    rationale: "Good enough for MVP, can be enhanced with ML later if needed"
    alternatives: ["Manual category selection only", "ML-based categorization"]
  - id: undo-duration
    choice: "5 second undo window"
    rationale: "Standard pattern, long enough to react but not intrusive"
    alternatives: ["3 seconds (too short)", "10 seconds (too long)"]
  - id: bulk-paste-parsing
    choice: "Parse on each keystroke with immediate feedback"
    rationale: "User sees results immediately, can fix issues before import"
    alternatives: ["Parse only on Import click"]
---

# Phase 02 Plan 04: Ingredient Entry Components Summary

**One-liner:** Natural language ingredient entry with autocomplete, bulk paste import, and drag-drop reordering with mobile support.

## What Was Built

Created three core ingredient components for recipe management:

1. **IngredientEntry** - Hybrid natural language + structured entry
2. **BulkPasteDialog** - Multi-line ingredient import with parsing preview
3. **IngredientList** - Drag-drop reordering with undo delete

## User Experience Flow

**Single ingredient entry:**
1. User types "2 cups flour" in text field
2. Press Tab or Enter → parsed into structured fields (quantity: 2, unit: cups, name: flour)
3. Category auto-suggested as "Pantry" based on ingredient name
4. User can edit any field before adding
5. Click Add → ingredient appears in list below

**Bulk paste:**
1. User clicks "Bulk Paste" button
2. Pastes shopping list from email/recipe site:
   ```
   2 cups flour
   1/2 lb chicken breast
   3 cloves garlic, minced
   ```
3. Sees parsed results immediately with warnings for incomplete parses
4. Can remove individual ingredients from preview
5. Click Import → all ingredients added to list

**List management:**
1. Ingredients displayed with formatted quantities (1/2, 1 1/4, etc.)
2. Color-coded category chips (Meat=red, Produce=green, Dairy=yellow)
3. Drag ingredient to reorder
4. Click delete → snackbar appears with "Undo" button for 5 seconds
5. Undo restores ingredient to original position

## Technical Implementation

### CategoryService
```csharp
public interface ICategoryService
{
    Task<List<Category>> GetCategoriesAsync(int householdId, CancellationToken cancellationToken = default);
}
```

Loads categories dynamically from database (not hardcoded). Categories created in Plan 02-01 seeding:
- Meat, Produce, Dairy, Pantry, Spices, Frozen, Bakery, Beverages, Other

### IngredientEntry Component
- **Parsing**: Uses IngredientParser service from Plan 02-02
- **Autocomplete**:
  - Units: Local static list (cups, tbsp, oz, lb, etc.)
  - Ingredients: Live query from RecipeService.GetIngredientSuggestionsAsync (household's previous entries)
- **Category suggestion**: Keyword-based heuristics (chicken→Meat, tomato→Produce, milk→Dairy)
- **Dialog integration**: MudBlazor DialogService with DialogParameters

### BulkPasteDialog Component
- **Real-time parsing**: OnParametersSet watches _pastedText and re-parses on change
- **Validation**: Flags ingredients with IsComplete=false (missing name)
- **Remove items**: User can delete individual parsed ingredients before import
- **Return type**: DialogResult.Ok with List<RecipeIngredient>

### IngredientList Component
- **Drag-drop**: Plk.Blazor.DragDrop.Dropzone with InstantReplace=true
- **Mobile support**: mobile-drag-drop polyfill loaded in App.razor
- **Fraction display**: FormatQuantity converts 0.5→"1/2", 1.5→"1 1/2", etc.
- **Undo delete**:
  - Stores _deletedIngredient and _deletedIndex
  - Snackbar with OnClick action button
  - Restores to original position (not end of list)

## Category Suggestion Heuristics

Simple keyword matching for MVP:
- **Meat**: chicken, beef, pork, fish, salmon, shrimp, bacon, sausage, turkey
- **Dairy**: milk, cheese, cream, butter, yogurt, egg
- **Produce**: lettuce, tomato, onion, garlic, pepper, carrot, celery, potato, broccoli, spinach, cucumber, avocado
- **Spices**: salt, pepper, cumin, paprika, oregano, basil, thyme, rosemary, cinnamon
- **Bakery**: bread, bun, roll, tortilla, bagel
- **Frozen**: frozen (keyword in name)
- **Pantry**: Default fallback

Can be enhanced with ML categorization in future if needed.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 2 - Missing Critical] Added IsComplete property to ParsedIngredient**
- **Found during:** Task 3 (BulkPasteDialog creation)
- **Issue:** BulkPasteDialog references ParsedIngredient.IsComplete to warn on incomplete parses, but property didn't exist
- **Fix:** Added computed property `public bool IsComplete => !string.IsNullOrWhiteSpace(Name);`
- **Files modified:** src/FamilyCoordinationApp/Services/IngredientParser.cs
- **Commit:** Included in e2c4234

**2. [Rule 2 - Missing Critical] Created CategoryService**
- **Found during:** Task 2 (IngredientEntry creation)
- **Issue:** Plan referenced ICategoryService which didn't exist yet (parallel Plan 02-03 was running)
- **Fix:** Created CategoryService with GetCategoriesAsync method
- **Files modified:** src/FamilyCoordinationApp/Services/CategoryService.cs, Program.cs
- **Commit:** Included in e2c4234

**3. [Rule 1 - Bug] Fixed MudBlazor dialog API usage**
- **Found during:** Task 2 and 3 compilation
- **Issue:**
  - DialogParameters generic syntax incorrect for MudBlazor 8.15.0
  - MudDialogInstance type doesn't exist, should be IDialogReference
  - Dialog.Cancel() doesn't exist, should be Close()
- **Fix:**
  - Changed `DialogParameters<T>` → `DialogParameters` with nameof() for parameter names
  - Changed `MudDialogInstance` → `IDialogReference?`
  - Changed `Cancel()` and `Close(DialogResult)` → `Close()` and `Close(DialogResult)`
- **Files modified:** IngredientEntry.razor, BulkPasteDialog.razor
- **Commit:** Included in e2c4234

**4. [Rule 1 - Bug] Fixed MudBlazor snackbar API**
- **Found during:** Task 4 compilation
- **Issue:** Property name was `Onclick` but should be `OnClick` (capital C)
- **Fix:** Changed `config.Onclick` → `config.OnClick`
- **Files modified:** IngredientList.razor
- **Commit:** Included in d63bda2

**5. [Rule 3 - Blocking] Removed non-existent AddBlazorDragDrop service registration**
- **Found during:** Task 1 compilation
- **Issue:** Plan called for `builder.Services.AddBlazorDragDrop()` but package doesn't provide this extension method
- **Fix:** Removed service registration (package works without explicit registration)
- **Files modified:** src/FamilyCoordinationApp/Program.cs
- **Commit:** Included in 1e8569a

## Testing Considerations

**Not tested yet (requires Plan 02-05 recipe CRUD pages):**
- Ingredient autocomplete actually queries household's previous entries
- Drag-drop reordering persists to database
- Category colors match user expectations
- Mobile drag-drop works on touch devices
- Undo timing (5 seconds) feels right

**Manual testing needed:**
- Bulk paste with various formats (with/without quantities, with notes, Unicode fractions)
- Category auto-suggestion accuracy
- Edge cases (empty paste, malformed ingredients, very long lists)

## Performance Notes

- **Category loading**: Single query on component init, cached in component
- **Ingredient autocomplete**: Debounced 300ms, min 2 characters, max 20 results
- **Bulk paste parsing**: Happens on each keystroke (could be debounced if performance issue with large pastes)
- **Drag-drop**: InstantReplace=true provides immediate visual feedback

## Next Phase Readiness

**Blocks:** None

**Concerns:**
- Drag-drop reordering not tested on actual mobile devices (polyfill may need configuration)
- Category heuristics may need tuning based on user feedback
- Bulk paste performance with 50+ ingredients untested

**Ready for:**
- Plan 02-05: Recipe CRUD pages can integrate these components
- Plan 04-01: Shopping list can reuse category logic and color scheme

## Lessons Learned

1. **Check MudBlazor API docs**: Dialog and Snackbar APIs differ from documentation examples (may be version differences)
2. **Component library imports**: Plk.Blazor.DragDrop namespace works via _Imports.razor, but fully qualifying component name avoids ambiguity
3. **Parallel plan coordination**: Creating service stubs for missing dependencies (CategoryService) keeps execution moving while parallel plans complete
4. **Heuristic-based features**: Simple keyword matching for category suggestion is "good enough" for MVP, avoid premature ML complexity

## Commits

1. **1e8569a** - chore(02-04): add blazor-dragdrop package and mobile polyfill
2. **e2c4234** - feat(02-04): create ingredient entry and bulk paste components
3. **d63bda2** - feat(02-04): create ingredient list with drag-drop and undo

**Total changes:**
- 4 files created
- 5 files modified
- ~700 lines of code added
