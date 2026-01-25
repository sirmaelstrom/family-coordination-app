# Phase 7 Context: Mobile & UX Polish

**Phase:** 7 of 7
**Goal:** PWA support, touch optimization, and sync status indicators

## Implementation Decisions

### PWA Installation (MOBILE-03)

**Display mode:** `standalone` — App should feel native, no browser chrome
**Theme color:** Match existing dark gray theme (`#1e1e1e` or similar from MudBlazor config)
**Icons:** Generate standard PWA icon set (192x192, 512x512) — simple rose/meal icon
**Offline strategy:** Cache app shell (HTML, CSS, JS) + show "offline mode" banner when disconnected
**Install prompt:** Don't force it — let browser's native "Add to Home Screen" handle it

**Rationale:** Family members will install once and use repeatedly. Standalone mode removes browser distractions at the grocery store.

### Touch Targets (MOBILE-02)

**Minimum size:** 40px × 40px for all interactive elements
**Focus areas:**
- Shopping list checkboxes (critical — used at store with full hands)
- Recipe card actions (expand, edit, delete)
- Meal plan slots (add/remove meals)
- Navigation items

**Implementation:** MudBlazor's `Size` parameter on components, CSS overrides where needed

**Rationale:** WCAG 2.1 recommends 44px, we'll target 40px minimum as a floor. Shopping list is highest priority since it's used while walking through aisles.

### Responsive Layout (MOBILE-01)

**Approach:** Verify existing MudBlazor responsive utilities work correctly
**Critical pages to test:**
1. Shopping list (grocery store use case)
2. Meal plan (quick reference)
3. Recipe list (browsing)
4. Recipe edit (less critical — usually done at home)

**Breakpoints:** Use MudBlazor defaults (xs < 600px, sm < 960px, etc.)
**Layout fixes:** Stack buttons vertically on mobile, hide secondary info, larger fonts for lists

**Rationale:** MudBlazor handles most responsive work. Focus on verifying and fixing edge cases rather than rebuilding.

### Sync Status (MOBILE-04)

**Location:** Header, near presence indicators
**States:**
- ✓ Synced (green checkmark, subtle)
- ⟳ Syncing (spinner, brief)
- ⚠ Offline/Error (amber warning, persistent until resolved)

**Behavior:**
- Don't flash on every poll (only show spinner if sync takes > 500ms)
- Offline mode: Show banner, allow viewing cached data, queue changes
- Error recovery: Auto-retry with exponential backoff, show "Retry" button after 3 failures

**Rationale:** Non-intrusive in normal operation, clear when something's wrong. The grocery store has spotty WiFi — users need confidence their changes saved.

---

## What NOT to build (defer)

- Offline data editing (cache static assets only, not full offline mode)
- Push notifications (no server infrastructure for this yet)
- Biometric auth (Google OAuth is sufficient)
- Custom icon design (use simple generated icons for MVP)

## Dependencies

- Phase 5 PollingService already handles sync polling
- Phase 5 DataNotifier already handles change detection
- MudBlazor already provides responsive grid

---
*Created: 2026-01-24*
