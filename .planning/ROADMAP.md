# Roadmap: Family Coordination App

## Overview

This roadmap delivers the integrated meal planning workflow: recipes → meal plan → shopping list. Starting with data foundation and authentication (Phase 1), building recipe management (Phase 2), enabling weekly meal planning (Phase 3), generating shopping lists with smart consolidation (Phase 4), adding multi-user household collaboration (Phase 5), implementing URL-based recipe import (Phase 6), and polishing mobile experience (Phase 7). Each phase delivers a complete, verifiable capability that builds toward reducing family coordination overhead.

## Phases

**Phase Numbering:**
- Integer phases (1, 2, 3): Planned milestone work
- Decimal phases (2.1, 2.2): Urgent insertions (marked with INSERTED)

Decimal phases appear between their surrounding integers in numeric order.

- [x] **Phase 1: Foundation & Infrastructure** - Database schema, authentication, and data access patterns
- [x] **Phase 2: Recipe Management** - Complete recipe CRUD with manual entry
- [x] **Phase 3: Meal Planning** - Weekly calendar with recipe assignment
- [x] **Phase 4: Shopping List Core** - Auto-generation with smart consolidation
- [x] **Phase 5: Multi-User Collaboration** - Household sharing with polling-based sync
- [x] **Phase 6: Recipe Import** - URL scraping with automatic parsing
- [x] **Phase 7: Mobile & UX Polish** - PWA support and touch optimization
- [x] ~~**Phase 8: Collaboration Wiring Fixes**~~ - DEPRECATED: Minor gaps, not worth effort
- [x] ~~**Phase 9: UX Enhancements**~~ - DEPRECATED: Minor gaps, not worth effort
- [x] **Phase 10: Chore & Task Management** - Shared chore board: claim/hand-off/complete state machine, recurrence, effort tiers, rooms (chores v1.0)
- [x] **Phase 11: Chores v1.1 — Equity View & Weekly Discord Digest** - Household effort-distribution lens + cron-triggered weekly digest, edit-chore dialog, starter backfill
- [x] **Phase 12: Chores v1.2 — Finish the Surface** - COMPLETE (2026-05-31): icons, home card, photo display/capture, room manager
- [ ] **Phase 13: Chores v1.3 — Multi-room chores** - PLANNED (from prod feedback #6): one chore belonging to multiple rooms (M:N) — needs a spec
- [x] **Phase 14: Chores v1.4 — Board simplification + subtask checklist** - COMPLETE (2026-06-14, PRs #41–42): collapse the lens/sub-chip duplication into a single filter axis (Up for grabs / Mine / All) with always-on attention sectioning + on-demand organizers (Rooms kept, Equity demoted); plus a per-chore subtask checklist (last-write-wins, never gates completion, resets on recurrence completion). Spec: `workshops/chores-v1.4-views-subtasks`
- [ ] **Phase 16: Chores v1.6 — Snooze & set-next-due** - BUILT (2026-06-22, this branch): a tz-resolved `Chore.SnoozedUntil` floor under next-due (the "not today" lever) — suppresses a chore until its date, skips a Fixed cadence slot it precedes, reschedules a OneOff, and gives recurring chores a first-due-on-create (the burst fix). Excluded from all four attention surfaces (board/home/equity/digest). `PATCH /api/chores/{id}/snooze` + island snooze menu/chip + edit-sheet "next due" / add-sheet "first due". Spec: `workshops/chores-snooze-and-reschedule`. Automated gates green (632 tests); V15 browser-verify is the remaining manual step.
- [ ] **Phase 15: Chores v1.5 — Equity rework (invisible labor)** - GRILLED → BUILD (2026-06-14). Reframe: be plain about what numbers mean + credit planning/coordination labor the DB ALREADY attributes (no new user logging). A read-only prod probe proved the case: Natalie set up 49% of the board + most shopping curation, yet the physical-points lens ranks her ~3%. Plan = un-blended labeled tallies (no exchange rate), capacity-aware physical share, on-demand prominence. Slice stack: (1) rename→"Physical board-load (this week)"+demote, (2) capacity-aware physical share, (3) planning footprint lanes [spec target]; (4) capacity-aware suggestion mechanism [follow-up]. Probe deferred the MealPlanEntry creator migration (no meal-plan signal). KB: `chores-equity-rework-phase-15-grill-resolved-prod-probe-verdict-build`.

## Phase Details

### Phase 1: Foundation & Infrastructure
**Goal**: Establish database schema with multi-tenant isolation and authenticated access
**Depends on**: Nothing (first phase)
**Requirements**: DATA-01, DATA-02, DATA-03, DATA-04, AUTH-01, AUTH-02, AUTH-03, AUTH-04
**Success Criteria** (what must be TRUE):
  1. User can sign in with Google OAuth and see their email displayed
  2. User session persists across browser restarts without re-authentication
  3. User can log out from any page and be redirected to login
  4. Database enforces composite foreign keys preventing cross-household data access
  5. Application runs on [SERVER] via Docker Compose accessible at your-domain.example.com
**Plans**: 4 plans

Plans:
- [x] 01-01-PLAN.md — Project setup with EF Core PostgreSQL and composite key schema
- [x] 01-02-PLAN.md — Google OAuth authentication with email whitelist authorization
- [x] 01-03-PLAN.md — Docker Compose configuration with nginx WebSocket support
- [x] 01-04-PLAN.md — First-run setup wizard, whitelist admin, and seed data

### Phase 2: Recipe Management
**Goal**: Users can create, view, edit, and delete recipes with structured ingredients
**Depends on**: Phase 1
**Requirements**: RECIPE-02, RECIPE-03, RECIPE-04, RECIPE-05, RECIPE-06
**Success Criteria** (what must be TRUE):
  1. User can manually create recipe with name, ingredients (quantity, unit, category), and instructions
  2. User can view list of all recipes in their household
  3. User can edit existing recipe and changes persist
  4. User can delete recipe with confirmation prompt
  5. Recipe ingredients display with proper categorization (Meat, Produce, Dairy, Pantry, Spices)
**Plans**: 7 plans

Plans:
- [x] 02-01-PLAN.md — MudBlazor setup, Category entity, SignalR file upload config
- [x] 02-02-PLAN.md — Ingredient parser (TDD) for natural language parsing
- [x] 02-03-PLAN.md — ImageService and RecipeService for backend CRUD
- [x] 02-04-PLAN.md — Ingredient entry component with parsing and drag-drop
- [x] 02-05-PLAN.md — Recipe list page with card grid and search
- [x] 02-06-PLAN.md — Recipe create/edit form with auto-save drafts
- [x] 02-07-PLAN.md — Category management settings and human verification

### Phase 3: Meal Planning
**Goal**: Users can assign recipes to weekly meal plan in calendar and list views
**Depends on**: Phase 2
**Requirements**: MEAL-01, MEAL-02, MEAL-03, MEAL-04, MEAL-05, MEAL-06
**Success Criteria** (what must be TRUE):
  1. User can view weekly meal plan in calendar format showing 7-day grid
  2. User can view weekly meal plan in list format on mobile
  3. User can click to assign recipe to specific date and meal type (Breakfast/Lunch/Dinner)
  4. User can add custom meal without recipe (e.g., "Leftovers", "Eating out")
  5. User can remove meal from plan
  6. User can click meal in plan to view full recipe details
**Plans**: 4 plans

Plans:
- [x] 03-01-PLAN.md — MealPlanService with get-or-create and CRUD operations
- [x] 03-02-PLAN.md — MealSlot and MealPlanNavigation shared components
- [x] 03-03-PLAN.md — RecipePickerDialog and calendar/list view components
- [x] 03-04-PLAN.md — MealPlan page with responsive views and navigation link

### Phase 4: Shopping List Core
**Goal**: Users can generate shopping list from meal plan with automatic ingredient consolidation
**Depends on**: Phase 3
**Requirements**: SHOP-01, SHOP-02, SHOP-03, SHOP-04, SHOP-05, SHOP-06, SHOP-07, SHOP-08, SHOP-09, SHOP-10
**Success Criteria** (what must be TRUE):
  1. User can generate shopping list from current week's meal plan with one click
  2. Shopping list consolidates duplicate ingredients from multiple recipes (e.g., "2 lbs chicken" + "1 lb chicken" = "3 lbs chicken")
  3. Shopping list groups items by category matching store layout (Meat, Produce, Dairy, Pantry, Spices)
  4. User can manually add items to shopping list (e.g., "Paper towels", "Dog food")
  5. User can edit item quantity and name in shopping list
  6. User can check off items while shopping and unchecked items stay at top, checked items grayed at bottom
  7. User can uncheck items if wrong item picked up
  8. User can delete items from shopping list
**Plans**: 6 plans

Plans:
- [x] 04-01-PLAN.md — UnitConverter service (TDD) for cooking measurement conversions
- [x] 04-02-PLAN.md — ShoppingListService for CRUD operations
- [x] 04-03-PLAN.md — ShoppingListGenerator with consolidation logic
- [x] 04-04-PLAN.md — Shopping list UI components (item row, category section, add dialog)
- [x] 04-05-PLAN.md — ShoppingList page with full workflow integration
- [x] 04-06-PLAN.md — Human verification of shopping list workflow

### Phase 5: Multi-User Collaboration
**Goal**: Multiple family members can access shared household data with polling-based sync
**Depends on**: Phase 4
**Requirements**: COLLAB-01, COLLAB-02, COLLAB-03, COLLAB-04
**Success Criteria** (what must be TRUE):
  1. Multiple users with whitelisted emails can sign in and access same household data
  2. User sees changes made by others when refreshing page or navigating between pages
  3. User can see who added each shopping list item with visual indicator
  4. User can see who created each recipe in recipe list
  5. Changes persist across all devices in household after polling refresh
**Plans**: 7 plans

Plans:
- [x] 05-01-PLAN.md — Schema updates: concurrency tokens, user profile fields, change tracking
- [x] 05-02-PLAN.md — Google OAuth picture claim mapping and initials computation
- [x] 05-03-PLAN.md — DataNotifier, PresenceService, and PollingService infrastructure
- [x] 05-04-PLAN.md — UserAvatar and PresenceBadge components
- [x] 05-05-PLAN.md — MainLayout presence/sync indicators and heartbeat integration
- [x] 05-06-PLAN.md — Attribution display on recipes and shopping list with auto-refresh
- [x] 05-07-PLAN.md — Optimistic concurrency with "checked wins" and human verification

### Phase 6: Recipe Import
**Goal**: Users can import recipes from URLs with automatic parsing
**Depends on**: Phase 2
**Requirements**: RECIPE-01
**Success Criteria** (what must be TRUE):
  1. User can paste recipe URL and automatically import name, ingredients, and instructions
  2. Import extracts recipe data from schema.org JSON-LD when available
  3. Import gracefully degrades to manual entry when parsing fails with clear error message
  4. Imported recipe appears in recipe list with indicator showing it was imported
  5. Import works for at least 80% of popular recipe sites (AllRecipes, Food Network, NYT Cooking)
**Plans**: 6 plans

Plans:
- [x] 06-01-PLAN.md — Infrastructure setup: AngleSharp, Polly packages, RecipeSchema POCO, UrlValidator
- [x] 06-02-PLAN.md — RecipeScraperService: HTTP fetch with Polly resilience, JSON-LD extraction with AngleSharp
- [x] 06-03-PLAN.md — RecipeImportService: URL → Recipe entity orchestration with graceful error handling
- [x] 06-04-PLAN.md — ImportRecipeDialog: URL input UI with progress and error states
- [x] 06-05-PLAN.md — Import indicator: source icon on recipe cards, clickable source link
- [x] 06-06-PLAN.md — Human verification of recipe import workflow

### Phase 7: Mobile & UX Polish
**Goal**: Application provides optimized mobile experience with PWA support
**Depends on**: Phase 5
**Requirements**: MOBILE-01, MOBILE-02, MOBILE-03, MOBILE-04
**Success Criteria** (what must be TRUE):
  1. Application displays correctly on mobile phones with responsive layout
  2. Shopping list checkboxes have minimum 40px touch targets on mobile
  3. User can install application as PWA via "Add to Home Screen"
  4. Application shows sync status indicator (loading spinner, "synced" checkmark, error message)
  5. Application loads initial view in under 3 seconds on Slow 3G connection
**Plans**: 5 plans

Plans:
- [x] 07-01-PLAN.md — PWA infrastructure: manifest.json, service worker, icons
- [x] 07-02-PLAN.md — Touch target optimization for shopping list and buttons
- [x] 07-03-PLAN.md — Responsive layout verification and fixes
- [x] 07-04-PLAN.md — Sync status indicator in header
- [x] 07-05-PLAN.md — Human verification of mobile experience

### Phase 8 & 9: DEPRECATED
Phases 8 and 9 were deprecated 2026-02-08. The identified gaps (attribution tracking, meal plan auto-refresh, drag-drop reorder, ingredient normalization) are minor and the app works well without them.

### Phase 10: Chore & Task Management
**Goal**: A shared household chore board with a claim / hand-off / complete state machine, recurrence, effort tiers, and rooms (chores v1.0)
**Status**: Delivered — merged via PR #11 (`feat/phase10-chore-task-management`, merge `eda3363`)
**Notes**: Optimistic concurrency (Postgres `xmin`), multi-tenant composite keys, Svelte island board behind the `CHORES_USE_ISLAND` flag. Detailed planning artifacts were not committed to `.planning/phases/` (live in branch history).

### Phase 11: Chores v1.1 — Equity View & Weekly Discord Digest
**Goal**: Surface household load fairly, and deliver it on a weekly cadence
**Depends on**: Phase 10
**Status**: Delivered — this branch. Planning artifacts: `.planning/phases/11-chores-v1.1-equity-digest/`
**Delivered**:
  - Equity distribution lens (effort-weighted, equal-share reference, week/all windows)
  - Weekly Discord digest: external-cron trigger → `POST /api/chores/digest/run` (shared-secret header token, idempotent, concurrency-safe atomic claim), webhook encrypted at rest, neutral framing, **no @mentions**
  - Edit-chore dialog, "Load starter chores" backfill, digest-settings UI
  - Go-live fix: digest trigger token mapped into the app container (`docker-compose.yml`) so the feature isn't dead-on-arrival in prod

### Phase 12: Chores v1.2 — Finish the Surface (PLANNED)
**Goal**: Close the Phase-10 UI seams + chore-board polish surfaced during the v1.1 review (2026-05-31)
**Depends on**: Phase 11
**Candidate scope** (grounded against the code, with size estimates):
  - ~~**Room-create UI** (MEDIUM)~~ — DONE (v1.2): inline "+ New room" in the chore add/edit sheets (creates via `POST /api/rooms` + selects, with an icon picker)
  - ~~**Room manager** (MEDIUM)~~ — DONE (v1.2, PR #21): in-island "Manage rooms" surface — reorder (svelte-dnd-action), delete (chores→General confirm), thin Add; per-room rename/icon/photo delegated to `RoomEditSheet`.
  - ~~**Chore icons** (MEDIUM)~~ — DONE (v1.2, PR #14): optional emoji `Chore.Icon` (parity with `Room.Icon`), full vertical slice through the M9 board-contract lockstep (`board.json` + `ChoreBoardDtoContractTests` + island `types.ts`); both sheets reuse `IconPicker`; renders leading the name on `ChoreCard`. Migration `AddChoreIcon` (additive, `""` default).
  - ~~**Home-page chore card** (LARGE)~~ — DONE (v1.2, PR #15): `Home.razor` injects `IChoreBoardService`; household-level overdue / due-today / up-for-grabs counts (collective, non-punitive framing) + a Chores Quick Action linking `/chores`.
  - ~~**Chore + room photo display** (LARGE)~~ — DONE (v1.2, PRs #18–20): tap-to-enlarge lightbox, chore-card thumbnails, room cover images (dashboard + drill hero), and room-photo capture (`RoomEditSheet`).
  - ~~**Equity up-for-grabs visual** (SMALL→MEDIUM)~~ — DONE (v1.2): `upForGrabsCount` / `fallingBehindCount` promoted from a text line to discrete dashed "outstanding work" entries in `EquityBoard.svelte` (a full proportional bar would still need an `UnassignedEffortPoints` field — deferred)
  - ~~**Room-create UI** (MEDIUM)~~ — DONE (v1.2): inline "+ New room" in the chore add/edit sheets
**Note**: distinct from the deprecated Phases 8 & 9 (recipe/meal/shopping polish); this is the chores track. v1.2 **COMPLETE** (2026-05-31): equity up-for-grabs + inline room-create (#13), chore icons (#14), home-page chore card (#15), photo display + capture (#18–20), room manager (#21). The chores surface is now functionally complete; next chores horizon = v1.3 multi-room chores (below) and/or the SvelteKit rewrite (unprioritized).

### Phase 13: Chores v1.3 — Multi-room chores (PLANNED)
**Goal**: Let a single chore belong to more than one room (e.g. "make all the beds", "dust all the rooms downstairs").
**Depends on**: Phase 12
**Source**: Production feedback #6 (in-app Feedback table, 2026-05-31).
**Why it's a phase, not a quick win**: `Chore.RoomId` is a single nullable FK today. Multi-room means a **many-to-many** `Chore ↔ Room` (join table + migration) that ripples through:
  - the board **rollup** logic (a chore now counts toward N rooms' due/total counts),
  - the island `roomGroups` grouping invariant (a chore would appear in multiple room groups — today it's in exactly one),
  - the **board DTO** (single `roomId` → a `roomIds` array; the M9 lockstep: DTO + `board.json` + `ChoreBoardDtoContractTests` + island `types.ts`),
  - the chore add/edit sheets (single room picker → multi-select),
  - the room-manager **delete→General** reassignment (a chore loses one room but may keep others).
**Recommendation**: `/spec` before building — it's a data-model + board-contract change with broad blast radius, not a surface tweak. Brushes the room model shipped in v1.2.

### Phase 14: Chores v1.4 — Board simplification + subtask checklist (COMPLETE)
**Goal**: Cut the navigation duplication and add a lightweight per-chore checklist. From prod feedback 2026-06-14.
**Status**: Delivered — merged 2026-06-14. PR #41 (`feat/chores-v1.4-board-simplify-subtasks`, merge `d9e4fda`) + follow-up PR #42 (`fix/chores-subtask-quickadd-and-followups`, merge `2a567d8`, subtask quick-add + prod-feedback fixes). Deploys via master→deploy.
**Source**: in-app feedback (2026-06-14). Spec: `D:\Development\data\outputs\workshops\chores-v1.4-views-subtasks\`.
**Two capabilities**:
  - **Board IA simplification (Model A)** — the 5 lenses (Needs attention / Rooms / Up for grabs / Mine / Equity) plus the 3 Needs-attention sub-chips (Everything / Up for grabs / Mine) conflate a *filter* (whose/what-state) with an *organizer* (how it's arranged) and duplicate "Up for grabs"/"Mine". Collapse to **one filter axis — Up for grabs · Mine · All** (default Up for grabs) — with the board **always** sectioning by attention (Falling behind / Due now / Coming up), and the organizers surfaced **on demand**: **Rooms kept accessible**, **Equity demoted** to a back-seat surface (it earns prominence back only via Phase 15). The per-user 📌 default machinery (already shipped) is reused, now pinning a filter state.
  - **Subtask checklist** — new `ChoreSubtask` entity (composite key `HouseholdId+ChoreId+SubtaskId`), **last-write-wins (no xmin, never bumps `Chore.Version`)** dedicated endpoints, **never gates completion**, **resets on the satisfying completion** of a recurring chore. Rides the one board payload → M9 contract lockstep (DTO + `board.json` + frozen key list + island `types.ts`).
**Execution**: roadmap-guided, gated autonomous build — small ordered stack, each unit ending in a local validation gate (build + targeted tests; browser-verify for UI), auto-continuing on green.

### Phase 16: Chores v1.6 — Snooze & set-next-due (BUILT — pending merge + V15)
**Goal**: Give a recurring chore a "not today" lever and a "set the next due date" lever without faking a completion (which pollutes equity) or deleting (which loses the schedule). Motivating prod case: "always Monday" mowing that doesn't always need doing; and a burst of new recurring chores all reading due-immediately on creation.
**Source**: production feedback / Spine quest "Update/Snooze when item not ready" (`bf9770c6-04bc-4141-ae8f-2064ba1ebff7`). Deep `/spec` pipeline (workshop `chores-snooze-and-reschedule`, validated 0 errors / 14 passed).
**Design**: ONE new field `Chore.SnoozedUntil` (`DateOnly?`, PascalCase column) = a tz-resolved **floor under next-due**. It never touches `DaysOfWeek`/`AnchorDate`/`IntervalDays` (so the weekday-conflict worry is structurally impossible). The computed `ChoreDuenessResult.IsSnoozed` is the single source of truth read by all four attention surfaces; cleared only by a satisfying completion (never on expiry).
**Delivered (6 WPs, gated-autonomous, this branch)**:
  - **Calculator** — universal suppression gate + Fixed skip-rule (`covered = … || s > slot`) + OneOff effective-due + `IsSnoozed` init-prop.
  - **Migration** — additive nullable `SnoozedUntil` `date` column.
  - **DTO** — `isSnoozed` + `snoozedUntil` across the M9 four-way lockstep (DTO + `board.json` + frozen key list + island `types.ts`).
  - **Service + endpoint** — `SnoozeAsync` + `PATCH /api/chores/{id}/snooze` (`{days|until|null, version}`, xmin/409, tz-resolved floor) + first-due/next-due on create/update + clear-on-complete.
  - **Attention guards** — snoozed chores excluded from board needs-attention / home counts / equity counts / weekly digest (single computed `IsSnoozed`).
  - **Island UI** — board snooze menu (Tomorrow / 3 days / 1 week / pick-a-date) + "Snoozed · due {resume}" chip + un-snooze; edit-sheet "Next due date"; add-sheet "First due".
**Status**: Automated gates green — `dotnet build` + full suite (632 tests) + island `svelte-check`/`vite build`. V15 browser-verify (manual, `CHORES_USE_ISLAND=true` + dev seed) is the remaining review-needed step before merge.

### Phase 15: Chores v1.5 — Equity rework / invisible labor (CAPTURED) + chore-history surface (MERGED)
**Goal**: Make the equity model measure the *real* distribution of household labor, including invisible/planning work, and actively help move toward equity.
**Source**: prod feedback 2026-06-14 (split out of the Phase 14 discussion). KB: "Chores Equity Rework — invisible/planning labor".
**Why it's a phase**: the current Equity lens counts only physical board completions (`EffortPoints`), which undercounts members whose contribution is planning/coordination/mental-load — invisible to the board. Rework needs (1) **new contribution dimensions** beyond physical completions and (2) **mechanisms that drive toward equity** (capacity-aware assignment, rebalancing nudges), not just a displayed distribution. Brushes `ChoreEquityCalculator` + the digest + possibly the work model. **Spec-first when prioritized** — the equity-measurement rungs (capacity-aware suggestion) are still open.

**Chore-history surface — the TEMPORAL face of Phase 15 (MERGED + DEPLOYED 2026-07-05, PR #70)**: a distinct, second face alongside the equity-measurement rungs — the *temporal* record of household labor. A browsable completion **ledger** (C, leads: `GET /api/chores/ledger`) with schedule-vs-completion **ghost rows** + a **gone-quiet** band, plus an evolved **logbook** (A: the digest-mirror recap additively grown with per-week distribution + milestones + kept moments + what-got-tended + the shared gone-quiet band). Collective / non-punitive — the per-person "B" trajectory stays dropped; framing is honest/pride-forward, not nagging. Backed by ONE shared `ChoreHistoryService` (DateOnly-space DST-correct projection, HouseholdId-scoped, window-bounded) feeding both endpoints; the snoozed-vs-slipped ghost reason folds the `ChoreSnoozeEvent` log (Phase-15 substrate, PR #69). Built via the 9-WP council-hardened spec (`data/outputs/workshops/fca-chore-history/`, Spine quest `598a1d49`), commit-per-WP. Full suite **855 green** + full `:8080` browser-verify PASS. No new EF migration (DTO-only; #69 shipped the substrate).

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6 → 7 (8 & 9 deprecated), then the Chores track: 10 → 11 → 12

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation & Infrastructure | 4/4 | Complete | 2026-01-23 |
| 2. Recipe Management | 7/7 | Complete | 2026-01-24 |
| 3. Meal Planning | 4/4 | Complete | 2026-01-24 |
| 4. Shopping List Core | 6/6 | Complete | 2026-01-24 |
| 5. Multi-User Collaboration | 7/7 | Complete | 2026-01-24 |
| 6. Recipe Import | 6/6 | Complete | 2026-01-24 |
| 7. Mobile & UX Polish | 5/5 | Complete | 2026-01-24 |
| 8. Collaboration Wiring Fixes | - | Deprecated | 2026-02-08 |
| 9. UX Enhancements | - | Deprecated | 2026-02-08 |
| 10. Chore & Task Management | - | Complete | 2026-05 (PR #11) |
| 11. Chores v1.1 (Equity & Digest) | 11 WPs | Complete | 2026-05-31 |
| 12. Chores v1.2 (Finish the Surface) | 6/6 items | Complete | PRs #13–15, #18–21 (2026-05-31) |
| 13. Chores v1.3 (Multi-room chores) | - | Planned | from prod feedback #6 — needs a spec |
| 14. Chores v1.4 (Board simplification + subtasks) | - | Complete | PRs #41–42 (2026-06-14) — Model A IA + subtask checklist |
| 15. Chores v1.5 (Equity rework / invisible labor) | - | Captured | equity rungs spec-first; **history surface MERGED — PR #70 (2026-07-05)** |
| 15h. Chore-history surface (ledger + logbook) | 9 WPs | Complete — Merged | PR #70 (2026-07-05, `8d738da`) — Spine `598a1d49`; 855 tests + browser-verify; CI + Deploy green |
| 16. Chores v1.6 (Snooze & set-next-due) | 6 WPs | Built — pending merge + V15 | 2026-06-22 (this branch) |

---
*Created: 2026-01-22*
*Last updated: 2026-07-05 (Phase 15 chore-history surface — ledger + logbook — MERGED + DEPLOYED via the 9-WP `fca-chore-history` spec, PR #70 → master `8d738da`; CI + Deploy both green. Full suite 855 green + `:8080` browser-verify PASS at build. Trust the Spine campaign "Family Coordination App" / quest `598a1d49` (now done) over this snapshot for live work-state.)*
