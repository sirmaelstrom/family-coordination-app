# Phase 2: Recipe Management - Context

**Gathered:** 2026-01-23
**Status:** Ready for planning

<domain>
## Phase Boundary

Users can create, view, edit, and delete recipes with structured ingredients. This includes the recipe list view, recipe detail/edit forms, ingredient entry with parsing, and category management. URL import is Phase 6. Multi-user attribution is Phase 5.

</domain>

<decisions>
## Implementation Decisions

### Recipe List Display
- Card-based layout with images, grid format
- Each card shows: image, name, ingredients preview (main ingredients at a glance)
- 3 cards per row on desktop, responsive on mobile
- Alphabetical sort by default
- Search box for filtering by name (no category filters in this phase)
- Clicking card expands in place (not modal, not separate page)
- Expanded card shows: full details, Edit button, Delete button, Add to Meal Plan button
- Empty state: friendly prompt with prominent "Add Recipe" button (no seed data shown)

### Ingredient Entry
- Hybrid entry: single line input that parses into structured fields
- Structured fields: quantity, unit, name, category, notes
- Autocomplete ingredient names from household's previous entries
- Auto-suggest category based on ingredient name (user can override)
- Quantities display as fractions (1/2, 1/4) not decimals
- Units: US + metric (cups, tbsp, tsp, lbs, oz, grams, ml, liters, each)
- Drag and drop to reorder ingredients
- Ingredient groups/sections supported (e.g., "For the sauce")
- Create groups by dragging ingredients into named sections
- Notes field per ingredient for substitutions/tips
- X button deletes with undo toast (few seconds to recover)
- Bulk paste: paste multiple lines, each becomes an ingredient
- Bulk paste shows original text alongside parsed result for verification
- Parse failures: populate what it can, highlight unparsed portions for user fix

### Recipe Form Layout
- Single scrolling page (not tabbed, not accordion)
- Fields: name, image, description (short intro), prep time, cook time, servings, source URL, ingredients, instructions
- Instructions: markdown-supported single text area
- Auto-save drafts as user types
- Delete in top toolbar (not bottom of form)
- Delete requires modal confirmation ("Are you sure?")

### Category Management
- Fixed defaults + custom categories allowed
- Default categories: Meat, Produce, Dairy, Pantry, Spices, Frozen, Bakery, Beverages, Other
- Quick add inline ("+ Add category" in dropdown) plus full management in Settings
- Categories have colors AND icons (emoji or SVG)
- Category display order is configurable (user can reorder to match their store layout)
- Categories are soft-deleted (can be restored from settings)
- Deleting category with ingredients prompts user to reassign them

### Image Management
- Upload from device + camera capture on mobile
- No image: food emoji placeholder based on recipe type
- Keep original image, CSS crops for display (no server-side crop)
- Max file size: 10 MB
- Server optimization: Claude's discretion (thumbnails vs compression vs none)

### Visual Design
- Dark mode first (soft dark gray background, not true black)
- Clean, minimal aesthetic
- Light mode deferred to Phase 7

### Claude's Discretion
- Loading skeleton design
- Exact spacing and typography
- Error state handling
- Image optimization strategy (thumbnails vs compression)
- Specific emoji selection for placeholders
- Dark mode color palette specifics

</decisions>

<specifics>
## Specific Ideas

- "Add to Meal Plan" shortcut directly from expanded recipe card (convenience for Phase 3 integration)
- Ingredient autocomplete should help maintain consistency across recipes (same ingredient name = easier shopping list consolidation in Phase 4)
- Bulk paste with original text visible helps when importing from websites/cookbooks
- Food emoji placeholders add personality without requiring images

</specifics>

<deferred>
## Deferred Ideas

- Light mode theme — Phase 7 (UX Polish)
- Recipe tags/categories for filtering — could be added later
- Advanced search (by ingredient, by time) — not in this phase

</deferred>

---

*Phase: 02-recipe-management*
*Context gathered: 2026-01-23*
