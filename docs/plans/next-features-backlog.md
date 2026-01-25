# Next Features Backlog

*Created: 2026-01-25 01:40 AM*
*Status: Active Development*

## Priority 1: Blocking Issues

### New Household Creation & Approval
**Problem:** realheathdev@gmail.com is whitelisted for Google API but gets "no access" because there's no way to create a new household after initial setup.

**Solution:**
- Allow authenticated users (not in a household) to request/create a new household
- Site admin approval workflow before household is active
- Future: monetization hooks, community "public" household

**Implementation:**
- [ ] New page: `/setup/new-household` for users without a household
- [ ] Household request stored with pending status
- [ ] Site admin page to approve/reject household requests
- [ ] Email notification on approval (optional)
- [ ] Redirect flow: no household → request form → pending → approved → active

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
2. 🔄 New household creation + approval (unblocks testing)
3. Recipe types
4. Auto-categorization improvements
5. Image management
6. Multi-recipe meal slots

---

*Roland working through these while Justin games 🎮*
