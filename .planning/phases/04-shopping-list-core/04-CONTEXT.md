# Phase 4: Shopping List Core - Context

**Gathered:** 2026-01-24
**Status:** Ready for planning

<domain>
## Phase Boundary

Generate shopping lists from weekly meal plans with automatic ingredient consolidation and in-store shopping support. Includes manual item management and multi-list capability. Recipe scaling and budget tracking are separate phases.

</domain>

<decisions>
## Implementation Decisions

### Consolidation Logic
- **Similar items**: Different cuts/types stay separate but visually grouped (e.g., "2 lbs chicken breast" + "1 lb chicken thighs" = separate items under "Chicken" category grouping)
- **Identical items**: Auto-combine and show source recipes (e.g., "3 cups milk (Pancakes, Mac & Cheese)")
- **Unit conversions**: Convert to common unit with note showing original units (e.g., "16 oz milk (1 cup + 8oz)")
- **Imprecise quantities**: Keep separate, user decides (e.g., "1 bunch cilantro" and "handful cilantro" as separate items)
- **Manual edits**: Track delta adjustments - remember "+1 cup" and preserve when re-consolidating
- **Consolidation toggle**: User preference setting (global) - set once, applies to all lists
- **Category boundary**: Different categories prevent consolidation (e.g., "Chicken Breast" in Meat vs "Chicken Stock" in Pantry remain separate)

### List Organization
- **Initial ordering**: Frequency/popularity within each category (most commonly bought items first)
- **Category reordering**: Store layout presets ("Kroger layout", "Walmart layout", "Custom") with drag-and-drop override
- **Item reordering**: Full drag-and-drop freedom (move items between categories, custom order within category)
- **Checked items**: Stay in place, grayed out (maintain position, always see full list)

### Manual Item Handling
- **Add UI**: Floating action button (FAB) + quick dialog (always-visible "+" button)
- **Category assignment**: Auto-suggest category based on item name, user can accept or change
- **Autocomplete**: Yes, from previous shopping list history (suggest items as user types)
- **Quantity handling**: Name required, quantity is optional extra field (simpler default flow)

### Shopping Workflow
- **Check-off interaction**: Tap anywhere on item row (larger touch target)
- **Undo mechanism**: Both tap-to-toggle AND snackbar undo ("Item checked. UNDO") for flexibility
- **Clear completed**: Menu action only (not prominent, available when needed)
- **Multi-list support**: Multiple lists with naming ("Weekly groceries", "Costco run", "Pharmacy")

### UX Priority
- **Critical**: Users should feel empowered by the app's ability to easily manage and organize their data
- Avoid painful workflows - prioritize speed and clarity
- Smart defaults with easy override options

### Claude's Discretion
- Exact frequency/popularity scoring algorithm
- Store layout preset definitions
- Visual design of category grouping headers
- Snackbar timing and styling
- Autocomplete ranking algorithm details
- Error state handling
- Loading states and transitions

</decisions>

<specifics>
## Specific Ideas

- Smart consolidation should feel helpful, not confusing - transparency is key (show source recipes)
- Multiple list support enables different shopping contexts (weekly grocery vs specialty store runs)
- Full drag-and-drop freedom gives users control without forcing manual organization

</specifics>

<deferred>
## Deferred Ideas

- Recipe scaling when adding to meal plan (would affect quantities) - future phase
- Budget tracking or price estimation - future phase
- Store-specific inventory availability - future phase
- Barcode scanning - future phase

</deferred>

---

*Phase: 04-shopping-list-core*
*Context gathered: 2026-01-24*
