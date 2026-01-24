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
- [ ] **Phase 5: Multi-User Collaboration** - Household sharing with polling-based sync
- [ ] **Phase 6: Recipe Import** - URL scraping with automatic parsing
- [ ] **Phase 7: Mobile & UX Polish** - PWA support and touch optimization

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
  5. Application runs on thedarktower via Docker Compose accessible at family.heathdev.me
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
- [ ] 05-01-PLAN.md — Schema updates: concurrency tokens, user profile fields, change tracking
- [ ] 05-02-PLAN.md — Google OAuth picture claim mapping and initials computation
- [ ] 05-03-PLAN.md — DataNotifier, PresenceService, and PollingService infrastructure
- [ ] 05-04-PLAN.md — UserAvatar and PresenceBadge components
- [ ] 05-05-PLAN.md — MainLayout presence/sync indicators and heartbeat integration
- [ ] 05-06-PLAN.md — Attribution display on recipes and shopping list with auto-refresh
- [ ] 05-07-PLAN.md — Optimistic concurrency with "checked wins" and human verification

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
**Plans**: TBD

Plans:
- [ ] (Plans defined during plan-phase)

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
**Plans**: TBD

Plans:
- [ ] (Plans defined during plan-phase)

## Progress

**Execution Order:**
Phases execute in numeric order: 1 → 2 → 3 → 4 → 5 → 6 → 7

| Phase | Plans Complete | Status | Completed |
|-------|----------------|--------|-----------|
| 1. Foundation & Infrastructure | 4/4 | Complete | 2026-01-23 |
| 2. Recipe Management | 7/7 | Complete | 2026-01-24 |
| 3. Meal Planning | 4/4 | Complete | 2026-01-24 |
| 4. Shopping List Core | 6/6 | Complete | 2026-01-24 |
| 5. Multi-User Collaboration | 0/7 | Not started | - |
| 6. Recipe Import | 0/? | Not started | - |
| 7. Mobile & UX Polish | 0/? | Not started | - |

---
*Created: 2026-01-22*
*Last updated: 2026-01-24*
