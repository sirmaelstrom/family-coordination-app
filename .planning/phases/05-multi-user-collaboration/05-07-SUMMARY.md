---
phase: 05-multi-user-collaboration
plan: 07
subsystem: concurrency
tags: [optimistic-concurrency, entity-framework, xmin, conflict-resolution]

# Dependency graph
requires:
  - phase: 05-01
    provides: "xmin concurrency tokens on entities"
  - phase: 05-06
    provides: "Real-time polling infrastructure"
provides:
  - "Checked wins concurrency strategy for shopping list items"
  - "ConflictIndicator component for resolved conflicts"
  - "Robust multi-user collaboration with graceful conflict handling"
affects: [Phase 6 (Recipe Scraping will need concurrency for imported recipes)]

# Tech tracking
tech-stack:
  added: []
  patterns:
    - "Checked wins merge strategy (application-level conflict resolution)"
    - "Retry loop with fresh entity fetch for concurrency exceptions"
    - "User-friendly conflict messages (not technical errors)"

key-files:
  created:
    - src/FamilyCoordinationApp/Components/ShoppingList/ConflictIndicator.razor
  modified:
    - src/FamilyCoordinationApp/Services/ShoppingListService.cs
    - src/FamilyCoordinationApp/Components/Pages/ShoppingList.razor
    - src/FamilyCoordinationApp/Services/RecipeService.cs

key-decisions:
  - "Checked wins strategy: if either user checked an item, it stays checked (optimistic behavior)"
  - "Last-write-wins for non-checkbox fields (name, quantity) with conflict indication"
  - "3-retry limit with fresh entity fetch to avoid disposed context errors"
  - "Snackbar notifications for conflicts (non-intrusive, 3-second duration)"

patterns-established:
  - "Fresh entity fetch in retry loop: context.ShoppingListItems.FirstOrDefaultAsync(i => i.Id == item.Id) instead of Attach"
  - "UpdatedAt set on all operations including soft deletes for polling detection"
  - "Tuple return (Success, WasConflict, ConflictMessage) for rich operation results"

# Metrics
duration: 18min
completed: 2026-01-24
---

# Phase 5 Plan 7: Optimistic Concurrency Summary

**"Checked wins" concurrency strategy with graceful conflict resolution for shopping list collaboration**

## Performance

- **Duration:** 18 min
- **Started:** 2026-01-24T13:48:12-06:00
- **Completed:** 2026-01-24T14:06:35-06:00
- **Tasks:** 4 (3 auto + 1 checkpoint)
- **Files modified:** 4

## Accomplishments
- Shopping list items handle concurrent check-offs without data loss
- "Checked wins" merge strategy ensures items stay checked if either user checked them
- DbUpdateConcurrencyException handled with retry and fresh entity fetch
- Conflict indicator component shows resolved conflicts to users
- Human-verified multi-user collaboration workflow with concurrent edits

## Task Commits

Each task was committed atomically:

1. **Task 1: Implement "checked wins" concurrency handling** - `f4ef261` (feat)
2. **Task 2: Create ConflictIndicator component** - `9154d8e` (feat)
3. **Task 3: Update ShoppingList page for concurrency** - `3c7740a` (feat)
4. **Task 4: Human verification checkpoint** - (approved after fixes)

**Fixes during verification:**
- `0966339` - Fix disposed context error by fetching fresh entity
- `e021724` - Fix auto-sync by ensuring UpdatedAt on recipe delete

**Plan metadata:** (pending - this commit)

## Files Created/Modified
- `src/FamilyCoordinationApp/Services/ShoppingListService.cs` - Added UpdateItemWithConcurrencyAsync with retry loop and checked wins strategy
- `src/FamilyCoordinationApp/Components/ShoppingList/ConflictIndicator.razor` - Warning chip component for resolved conflicts
- `src/FamilyCoordinationApp/Components/Pages/ShoppingList.razor` - Updated to use concurrency-aware updates with snackbar notifications
- `src/FamilyCoordinationApp/Services/RecipeService.cs` - Fixed UpdatedAt setting on recipe soft delete

## Decisions Made

**Checked wins strategy:** If either user checked an item, it stays checked. This optimistic behavior matches user expectations (progress is preserved, not lost).

**Last-write-wins for non-checkbox fields:** For name and quantity edits, the last save wins but users see a conflict notification. This is acceptable for family collaboration where simultaneous edits are rare.

**Fresh entity fetch in retry loop:** Instead of Attach (which can fail with disposed context), fetch the entity fresh from the database on each retry: `context.ShoppingListItems.FirstOrDefaultAsync(i => i.Id == item.Id)`.

**UpdatedAt on all operations:** Even soft deletes must set UpdatedAt to trigger polling detection. Without this, deleted recipes wouldn't trigger auto-refresh.

## Deviations from Plan

### Auto-fixed Issues

**1. [Rule 1 - Bug] Fixed DbContext disposed error in concurrency handler**
- **Found during:** Task 4 (Human verification)
- **Issue:** `UpdateItemWithConcurrencyAsync` used `Attach()` to track the entity, but when a concurrency exception occurred and the retry loop attempted to use the same context, it was disposed. This caused `ObjectDisposedException: Cannot access a disposed object` errors.
- **Fix:** Changed retry loop to fetch a fresh entity from the database instead of attaching: `var dbItem = await context.ShoppingListItems.FirstOrDefaultAsync(i => i.Id == item.Id)`. Then copy the proposed changes to the fresh entity before saving.
- **Files modified:** `src/FamilyCoordinationApp/Services/ShoppingListService.cs`
- **Verification:** Concurrent check-offs no longer crash with disposed context errors
- **Committed in:** `0966339`

**2. [Rule 2 - Missing Critical] Fixed auto-sync not working for recipe deletion**
- **Found during:** Task 4 (Human verification)
- **Issue:** When a recipe was soft deleted, the `UpdatedAt` field was not set, so the polling mechanism didn't detect the change. As a result, deleted recipes didn't disappear from other users' screens until manual refresh.
- **Fix:** Added `recipe.UpdatedAt = DateTime.UtcNow;` to the `DeleteRecipeAsync` method in `RecipeService.cs` before saving changes.
- **Files modified:** `src/FamilyCoordinationApp/Services/RecipeService.cs`
- **Verification:** Recipe deletions now trigger auto-refresh on all connected clients within 5 seconds
- **Committed in:** `e021724`

---

**Total deviations:** 2 auto-fixed (1 bug, 1 missing critical)
**Impact on plan:** Both fixes were necessary for correct operation. First fix prevented crashes during concurrent edits. Second fix ensured polling infrastructure worked consistently across all operations.

## Issues Encountered

**DbContext lifecycle in retry loops:** Initial implementation used `Attach()` which works for single-attempt saves but fails in retry scenarios where the context may be disposed. Resolved by fetching fresh entities on each retry attempt.

**UpdatedAt not set on all operations:** Initially missed setting `UpdatedAt` on recipe soft deletes, breaking polling detection. This highlighted the importance of consistently updating tracking fields across all mutation operations.

## User Setup Required

None - no external service configuration required.

## Next Phase Readiness

Phase 5 (Multi-User Collaboration) is now complete with:
- User attribution on all user-created content
- Online presence indicators
- Automatic sync via polling (5-second interval)
- Optimistic concurrency with graceful conflict handling
- Human-verified multi-user workflow

Ready to proceed to Phase 6 (Recipe Scraping) which will need to:
- Consider concurrency for imported recipes (use same xmin + UpdatedAt pattern)
- Respect UpdatedAt for polling when scraping updates existing recipes
- Attribute scraped recipes to the user who imported them

No blockers. Multi-user collaboration foundation is solid.

---
*Phase: 05-multi-user-collaboration*
*Completed: 2026-01-24*
