# Next Features Backlog

*Created: 2026-01-25*
*Last Updated: 2026-01-25 10:25 AM*
*Status: Active Development*

## ✅ Completed

### New Household Creation & Approval ✅
**Completed:** 2026-01-25

- ✅ `/household/request` — users without household can request one
- ✅ `/household/pending` — status page with auto-refresh
- ✅ `/settings/households` — admin approval/rejection workflow
- ✅ HouseholdRequest entity with Pending/Approved/Rejected status
- ✅ Default categories seeded on approval

---

## Priority 2: Recipe Enhancements

### Recipe Types (from earlier plan)
- [ ] Add RecipeType enum (Main, Side, Appetizer, Dessert, Beverage, Sauce, Other)
- [ ] Field on Recipe entity
- [ ] UI selector in editor
- [ ] Badge on cards

### Image Management
**Current:** Images uploaded per-recipe, stored in /uploads

**Desired:**
- Browse/select from previously uploaded images
- Image library/gallery view
- Reuse images across recipes
- Possibly use images in recipe content (inline)

**Implementation:**
- [ ] Image library service (list all uploaded images)
- [ ] Image picker dialog component
- [ ] Option to select existing vs upload new
- [ ] Future: inline images in instructions (markdown image support)

### Multi-Recipe Meal Slots (from earlier plan)
- [ ] MealPlanEntry supports multiple RecipeIds
- [ ] UI for adding multiple recipes to a slot
- [ ] Shopping list aggregation from all recipes

---

## Priority 3: Quality of Life

### Improved Auto-Categorization
**Problem:** Imported ingredients default to "Pantry" which isn't helpful

**Solution:**
- Smarter category inference from ingredient names
- Common mappings: milk/cheese/eggs → Dairy, chicken/beef → Meat, etc.
- Learn from user corrections over time (optional)

**Implementation:**
- [ ] Category inference service with keyword matching
- [ ] Default mappings for common ingredients
- [ ] Apply during recipe import
- [ ] Future: ML-based categorization from usage patterns

---

## Future Ideas (Not Prioritized)

### Demo Mode 🆕
- Read-only access for unauthorized/anonymous users
- Admin seeds and manages demo data (demo household)
- Showcases features without requiring signup
- Use case: sharing the app with potential users, portfolio showcase
- *Mechanics TBD*

### Community/Public Household
- Default "community" household anyone can join
- Public recipe sharing
- Moderation needed
- *Needs more thought*

### Monetization
- Premium features?
- Family plan pricing?
- *Way future*

---

## Working Order

1. ✅ Capture plan (this document)
2. ✅ New household creation + approval
3. Recipe types + multi-recipe meal slots
4. Auto-categorization for imported ingredients
5. Image management (library/picker)
6. Demo mode (future)

---

*Updated 2026-01-25*
