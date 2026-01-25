# Recipe List UX Rework

## Overview
Improve the recipe list layout to better utilize screen space and fix mobile tag positioning.

## Issues to Address

### 1. Mobile: Tags Don't Stay in Place
- Tags in flex columns shift inconsistently on narrow screens
- Need to audit tag container CSS and ensure consistent positioning

### 2. Desktop: Expanded Recipe Has Unused Space
- When a recipe is expanded, there's too much empty space
- Current: Expanded content is constrained to same narrow column
- Goal: Let expanded recipe "breathe" — use available horizontal space

## Proposed Solutions

### Mobile Tags Fix
- Investigate current flex behavior on tags
- Options:
  - Use `flex-wrap: wrap` with fixed min-width on tags
  - Switch to MudChipSet with consistent sizing
  - Position tags absolutely within card

### Desktop Expanded State
**Option A: Full-Width Expansion**
- When recipe is expanded, it breaks out of grid and spans full width
- Other recipes collapse or shift down
- Most dramatic visual change

**Option B: Two-Column Expanded Layout**  
- Expanded recipe uses 2-column internal layout
- Left: Image + metadata
- Right: Ingredients + instructions
- Recipe card stays in grid but content uses space better

**Option C: Slide-Out Panel**
- Clicking recipe opens a side panel (like MudDrawer)
- Panel is wider than card, shows full details
- Grid stays stable, panel overlays

## Recommended Approach
Start with **Option B** (two-column internal layout) because:
- Least disruptive to existing layout
- Works well on tablet/desktop
- Can gracefully fall back to single column on mobile
- MudBlazor has good responsive grid support

## Implementation Steps

### Phase 1: Audit & Fix Mobile Tags
1. [ ] Inspect current RecipeCard tag rendering
2. [ ] Identify flex issues causing position shifts
3. [ ] Implement consistent tag container
4. [ ] Test at 320px, 375px, 414px widths

### Phase 2: Desktop Expanded Layout
1. [ ] Add responsive breakpoint detection
2. [ ] Create two-column layout for expanded state (md+)
3. [ ] Left column: Image, title, metadata, tags
4. [ ] Right column: Ingredients, instructions
5. [ ] Test at 768px, 1024px, 1440px widths

### Phase 3: Polish
1. [ ] Smooth expand/collapse animations
2. [ ] Ensure focus management for accessibility
3. [ ] Test with various recipe content lengths

## Future Considerations (Out of Scope)
- Collapsible nav menu
- User-configurable nav placement
- Recipe card size preferences

---
*Created: 2026-01-25*
