# Requirements: Family Coordination App

**Defined:** 2026-01-22
**Core Value:** The integrated workflow - recipes → meal plan → shopping list. Automated aggregation and real-time collaboration reduce mental load from scattered information and last-minute decisions.

## v1 Requirements

Requirements for initial release. Each maps to roadmap phases.

### Authentication

- [x] **AUTH-01**: User can sign in with Google OAuth
- [x] **AUTH-02**: User email is validated against whitelist for family access
- [x] **AUTH-03**: User session persists across browser restarts
- [x] **AUTH-04**: User can log out from any page

### Recipe Management

- [x] **RECIPE-01**: User can import recipe from URL with automatic parsing (schema.org JSON-LD)
- [x] **RECIPE-02**: User can manually create recipe with name, ingredients, and instructions
- [x] **RECIPE-03**: Recipe ingredients include name, quantity, unit, and category
- [x] **RECIPE-04**: User can view list of all recipes
- [x] **RECIPE-05**: User can edit existing recipes
- [x] **RECIPE-06**: User can delete recipes (soft delete with confirmation)

### Meal Planning

- [x] **MEAL-01**: User can view weekly meal plan in calendar format (7-day grid)
- [x] **MEAL-02**: User can view weekly meal plan in list format (mobile-friendly)
- [x] **MEAL-03**: User can assign recipe to specific date and meal type (Breakfast/Lunch/Dinner)
- [x] **MEAL-04**: User can add custom meal without recipe ("Leftovers", "Eating out")
- [x] **MEAL-05**: User can remove meal from plan
- [x] **MEAL-06**: User can view recipe details from meal plan

### Shopping Lists

- [x] **SHOP-01**: User can generate shopping list from weekly meal plan
- [x] **SHOP-02**: Shopping list aggregates ingredients from multiple recipes with smart consolidation
- [x] **SHOP-03**: Shopping list groups items by category (Meat, Produce, Dairy, Pantry, Spices)
- [x] **SHOP-04**: User can manually add items to shopping list
- [x] **SHOP-05**: User can edit item quantity/name in shopping list
- [x] **SHOP-06**: User can check off items while shopping
- [x] **SHOP-07**: User can uncheck items (if picked up wrong item)
- [x] **SHOP-08**: User can delete items from shopping list
- [x] **SHOP-09**: Checked items appear grayed out at bottom of list
- [x] **SHOP-10**: Unchecked items appear at top of list

### Collaboration & Sync

- [x] **COLLAB-01**: Multiple family members can access same household data
- [x] **COLLAB-02**: Changes sync across devices when user refreshes (polling-based)
- [x] **COLLAB-03**: User can see who added each shopping list item
- [x] **COLLAB-04**: User can see who created each recipe

### Mobile & UX

- [x] **MOBILE-01**: Application is responsive and works on mobile phones
- [x] **MOBILE-02**: Touch targets are minimum 40px for shopping list checkboxes
- [x] **MOBILE-03**: Application supports PWA installation (Add to Home Screen)
- [x] **MOBILE-04**: Application shows sync status (loading, synced, error)

### Data & Infrastructure

- [x] **DATA-01**: All data includes HouseholdId for future multi-tenant expansion
- [x] **DATA-02**: Database enforces composite foreign keys (HouseholdId + EntityId)
- [x] **DATA-03**: Application uses DbContextFactory pattern (Blazor Server concurrency)
- [x] **DATA-04**: Components implement proper disposal (IAsyncDisposable)

## v2 Requirements

Deferred to future release. Tracked but not in current roadmap.

### Real-Time Collaboration (v1.1)

- **REALTIME-01**: Shopping list updates appear instantly without refresh (SignalR)
- **REALTIME-02**: User sees visual indicator when another user is editing
- **REALTIME-03**: Optimistic UI updates with conflict resolution

### Recipe Organization (Phase 2)

- **RECIPE-07**: User can tag recipes with categories (cuisine, dietary, difficulty)
- **RECIPE-08**: User can search recipes by name, ingredient, or tag
- **RECIPE-09**: User can filter recipes by tags
- **RECIPE-10**: User can organize recipes into collections

### Recipe Features (Phase 2)

- **RECIPE-11**: User can scale recipe servings and quantities adjust automatically
- **RECIPE-12**: User can upload recipe step images
- **RECIPE-13**: User can view recipe main image

### Meal Planning Enhancements (Phase 2)

- **MEAL-07**: User can add notes to meal ("Marinate overnight", "Double batch")
- **MEAL-08**: User can drag-drop to move meals between days
- **MEAL-09**: User can swap meals between days

### Shopping List Enhancements

- **SHOP-11**: User can add quick pantry staples from predefined list
- **SHOP-12**: Shopping list works offline and syncs when reconnected (v1.2)

### Google Calendar Integration (Phase 2)

- **CAL-01**: User can sync meal plan to Google Calendar
- **CAL-02**: Meal plan changes update calendar events automatically
- **CAL-03**: Calendar event includes recipe link and cook time

### To-Do Lists (Phase 2)

- **TODO-01**: User can create named to-do lists
- **TODO-02**: User can add items to to-do lists
- **TODO-03**: User can check off completed items
- **TODO-04**: To-do lists sync in real-time like shopping lists

### Advanced Features (Phase 3+)

- **ADVANCED-01**: User can track pantry inventory
- **ADVANCED-02**: User can mark meals as leftovers
- **ADVANCED-03**: Shopping list excludes ingredients in pantry

## Out of Scope

Explicitly excluded. Documented to prevent scope creep.

| Feature | Reason |
|---------|--------|
| Nutrition/calorie tracking | Different user intent - meal planning is about reducing decision fatigue, not fitness tracking. Causes retention drop and scope creep. Users can use MyFitnessPal separately if needed. |
| AI meal plan generation | Premature for MVP. Requires training data, complex NLP, users often reject auto-plans. Better to nail manual planning first, defer to v2+ after product-market fit. |
| Grocery delivery integration | Partnership complexity, API maintenance burden, not core differentiator. Users can export list to clipboard and paste into delivery app themselves. |
| Video recipe imports | Extremely fragile (transcription, extraction), high error rate, maintenance nightmare. Niche vs URL import. Focus on reliable web scraping instead. |
| Recipe rating/review system | Community management burden (moderation, spam). Scope creep to social network. Import recipes from sites with existing ratings, link to source for reviews. |
| Voice commands | Complex NLP, error-prone in noisy kitchens, low ROI. Focus on fast mobile interactions (quick-add buttons, swipe gestures) instead. |
| Multi-tenant onboarding | Phase 1 is single household (the family). Data model supports multi-tenant (HouseholdId), but onboarding/billing deferred until validated. |
| Email/password authentication | Google OAuth sufficient for family use. Email whitelist provides access control without password management complexity. |

## Traceability

Which phases cover which requirements. Updated during roadmap creation.

| Requirement | Phase | Status |
|-------------|-------|--------|
| AUTH-01 | Phase 1 | Complete |
| AUTH-02 | Phase 1 | Complete |
| AUTH-03 | Phase 1 | Complete |
| AUTH-04 | Phase 1 | Complete |
| DATA-01 | Phase 1 | Complete |
| DATA-02 | Phase 1 | Complete |
| DATA-03 | Phase 1 | Complete |
| DATA-04 | Phase 1 | Complete |
| RECIPE-02 | Phase 2 | Complete |
| RECIPE-03 | Phase 2 | Complete |
| RECIPE-04 | Phase 2 | Complete |
| RECIPE-05 | Phase 2 | Complete |
| RECIPE-06 | Phase 2 | Complete |
| MEAL-01 | Phase 3 | Complete |
| MEAL-02 | Phase 3 | Complete |
| MEAL-03 | Phase 3 | Complete |
| MEAL-04 | Phase 3 | Complete |
| MEAL-05 | Phase 3 | Complete |
| MEAL-06 | Phase 3 | Complete |
| SHOP-01 | Phase 4 | Complete |
| SHOP-02 | Phase 4 | Complete |
| SHOP-03 | Phase 4 | Complete |
| SHOP-04 | Phase 4 | Complete |
| SHOP-05 | Phase 4 | Complete |
| SHOP-06 | Phase 4 | Complete |
| SHOP-07 | Phase 4 | Complete |
| SHOP-08 | Phase 4 | Complete |
| SHOP-09 | Phase 4 | Complete |
| SHOP-10 | Phase 4 | Complete |
| COLLAB-01 | Phase 5 | Complete |
| COLLAB-02 | Phase 5 | Complete |
| COLLAB-03 | Phase 5 | Complete |
| COLLAB-04 | Phase 5 | Complete |
| RECIPE-01 | Phase 6 | Complete |
| MOBILE-01 | Phase 7 | Complete |
| MOBILE-02 | Phase 7 | Complete |
| MOBILE-03 | Phase 7 | Complete |
| MOBILE-04 | Phase 7 | Complete |

**Coverage:**
- v1 requirements: 34 total
- Mapped to phases: 34/34 (100%)
- Unmapped: 0

---
*Requirements defined: 2026-01-22*
*Last updated: 2026-01-25 — all v1 requirements complete*
