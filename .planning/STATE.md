# Project State

## Project Reference

See: .planning/PROJECT.md (updated 2026-01-22)

**Core value:** The integrated workflow - recipes → meal plan → shopping list. Automated aggregation and real-time collaboration reduce mental load from scattered information and last-minute decisions.
**Current focus:** Phase 4 - Shopping List Core

## Current Position

Phase: 5 of 7 (Multi-User Collaboration)
Plan: 2 of 7 (executing)
Status: In progress
Last activity: 2026-01-24 — Completed 05-02-PLAN.md

Progress: [████████████████] 64% (4.3 of 7 phases)

## Performance Metrics

**Velocity:**
- Total plans completed: 23
- Average duration: 8.0 min
- Total execution time: 3.1 hours

**By Phase:**

| Phase | Plans | Total | Avg/Plan |
|-------|-------|-------|----------|
| 1 | 4 | 16min | 4.0min |
| 2 | 7 | 32min | 4.6min |
| 3 | 4 | 90min | 22.5min |
| 4 | 6 | 34min | 5.7min |
| 5 | 2 | 2min | 1.0min |

**Recent Trend:**
- Last 5 plans: 11min, 4min, 3min (04-06 verification), 1min (05-01), 1min (05-02)
- Trend: Phase 5 starting efficiently with schema/config tasks (1min average)

*Updated after each plan completion*

## Accumulated Context

### Decisions

Decisions are logged in PROJECT.md Key Decisions table.
Recent decisions affecting current work:

- Blazor Server over Blazor WASM (C# expertise, built-in SignalR, simpler deployment)
- PostgreSQL over SQL Server (Linux deployment, open source, Docker-friendly)
- Single-tenant MVP with HouseholdId for future multi-tenant expansion
- Google OAuth only (family uses Google, no password reset complexity)
- DbContextFactory over scoped DbContext (required for Blazor Server thread safety) [01-01]
- Composite keys at database level (not application-only filtering) [01-01]
- HouseholdId-first ordering in all composite keys [01-01]
- String categories over enums for flexibility [01-01]
- DateOnly for meal plan dates (not DateTime) [01-01]
- 30-day cookie expiration with sliding expiration (session persistence) [01-02]
- Forwarded headers middleware first in pipeline (nginx reverse proxy) [01-02]
- POST endpoint for Google OAuth challenge (CSRF protection) [01-02]
- Two-stage Dockerfile over single-stage (smaller final image) [01-03]
- PostgreSQL health check dependency before app startup [01-03]
- nginx WebSocket configuration for Blazor Server SignalR [01-03]
- Middleware approach for setup redirect (checks all requests) [01-04]
- Any whitelisted user can manage whitelist (family trust model) [01-04]
- Seed data runs automatically in development only [01-04]
- MudBlazor for UI framework (Material Design, dark mode support) [02-01]
- Soft dark gray theme over true black (better contrast) [02-01]
- 12 MB SignalR message size for recipe image uploads [02-01]
- 9 default ingredient categories (Meat, Produce, Dairy, etc.) [02-01]
- String-based category references in RecipeIngredient [02-01]
- Soft delete pattern on Category entity with global query filter [02-01]
- Unicode fraction normalization over complex parsing [02-02]
- Tokenization-based parser over regex-only approach [02-02]
- Scoped service lifetime for IngredientParser [02-02]
- HashSet for unit lookup over switch statements [02-02]
- Streaming file upload over memory buffering (prevents OOM on large images) [02-03]
- Replace-all pattern for ingredient updates (simpler than change tracking) [02-03]
- StartsWith for autocomplete queries (leverages index vs Contains) [02-03]
- blazor-dragdrop over custom JavaScript (mature package with mobile support) [02-04]
- Keyword-based category suggestion over ML (good enough for MVP) [02-04]
- 5 second undo window (standard pattern, not too short or too long) [02-04]
- Real-time bulk paste parsing (immediate feedback vs parse on import) [02-04]
- Card-based grid layout with expand in place (no modal, maintains context) [02-05]
- Only one recipe card expanded at a time (prevents cluttered view) [02-05]
- 300ms debounce for search input (balances responsiveness with backend load) [02-05]
- Markdig for markdown rendering in recipe instructions (lightweight parser) [02-05]
- EventCallback.Factory.Create for parameterized callbacks (Blazor requirement) [02-05]
- 2-second auto-save debounce for form drafts (standard pattern, not too aggressive) [02-06]
- JSON serialization for draft data (simple and flexible) [02-06]
- User.Id foreign key for RecipeDraft (User has single Id primary key, not composite) [02-06]
- NavigationLock warns before leaving with unsaved changes [02-06]
- Draft restoration shows snackbar notification [02-06]
- Global InteractiveServer render mode for all pages [02-07]
- MudBlazor providers in MainLayout for interactive context [02-07]
- Nullable GoogleId with filtered unique index for pending users [02-07]
- Get-or-create pattern for weekly meal plans (simplifies UI logic) [03-01]
- Upsert logic in AddMealAsync (update if exists, create if not) [03-01]
- RecipeId XOR CustomMealName validation (one meal source per entry) [03-01]
- Hard delete for meal plan entries (planning data, not historical) [03-01]
- Monday-based week calculation (standard business week) [03-01]
- Event callbacks with proper propagation control for nested click handlers [03-02]
- Hover-only visibility for remove buttons [03-02]
- Public nested class for MealSelection (RecipePickerDialog.MealSelection pattern) [03-03]
- CSS Grid with vertical meal labels for space-efficient calendar layout [03-03]
- Lambda captures for entry-specific callbacks in event wiring [03-03]
- Explicit WeekStartDateChanged handler for week navigation data reload (not @bind) [03-04]
- MudHidden responsive switching between calendar and list views [03-04]
- RecipePickerDialog.MealSelection public nested class pattern for type-safe dialog results [03-04]
- Context-aware HandleClick in MealSlot (empty→picker, filled→details) [03-04]
- Edit button for changing meal assignments with hover visibility [03-04]
- @context pattern in Dropzone to prevent ingredient duplication [03-04]
- Lookup table for unit conversions over UnitsNet library (cooking has ~15 units, table is 50 lines) [04-01]
- Decimal rounding to 10 places after conversion (avoids floating-point precision errors) [04-01]
- Base unit conversion approach (linear scaling: N units = N table entries, not N²) [04-01]
- Count units (piece, can, bunch) don't convert to other families (incompatible measurement types) [04-01]
- Normalize ingredient names before grouping (removes descriptors like fresh, organic, chopped) [04-03]
- Category as consolidation boundary (different categories prevent consolidation) [04-03]
- Quantity delta tracking for preserving user adjustments during regeneration [04-03]
- Source recipe tracking in comma-separated fields (transparency for consolidation) [04-03]
- EventCallback<T> for component callbacks (child passes item directly vs parent tracking context) [04-04]
- Defer drag-drop to future enhancement (blazor-dragdrop conflicts with Razor auto-formatter) [04-04]
- Foreach loop over Dropzone for item rendering (workaround for formatter conflicts) [04-04]
- Fully qualified Data.Entities.ShoppingList to avoid namespace collision with page component [04-05]
- Local closure for undo snackbar (avoid shared field mutation pitfall per RESEARCH.md) [04-05]
- 4-second undo snackbar visibility (standard UX pattern) [04-05]
- FAB for add item (always-visible, mobile-friendly) [04-05]
- Category ordering by grocery store layout (Produce → Spices) [04-05]
- Default date range today through end of week (Sunday) for shopping list generation [04-06]
- Data protection keys in /root/.aspnet/DataProtection-Keys with volume mount (prevents logout on restart) [04-06]
- Image uploads path /app/wwwroot/uploads (matches published app structure) [04-06]
- Update picture URL and initials on every login (ensures Google profile changes reflected) [05-02]
- Compute initials from first and last name characters (standard convention for avatars) [05-02]

### Pending Todos

- Phase 5: Continue plan 05-03 (Avatar component)
- Phase 5: Plans 05-04 through 05-07 pending

### Blockers/Concerns

- Phase 4: blazor-dragdrop Dropzone component conflicts with Razor auto-formatter (workaround: defer drag-drop, use foreach)
- Phase 4: Razor auto-formatter aggressively rewrites nested component patterns (impacts Dropzone ChildContent usage)
- Phase 4: Simple normalization may miss some ingredient variations (monitor user feedback for false negatives)
- Phase 5: Conflict resolution strategy for concurrent edits (research flagged)
- Phase 6: Anti-bot mitigation for recipe scraping (research flagged)

## Session Continuity

Last session: 2026-01-24T19:21:37Z
Stopped at: Completed 05-02-PLAN.md (OAuth profile picture and initials)
Resume file: None
Next command: Execute 05-03-PLAN.md (Avatar component)

---
*Created: 2026-01-22*
*Last updated: 2026-01-24T19:21:37Z*
