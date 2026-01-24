# Phase 5: Multi-User Collaboration - Context

**Gathered:** 2026-01-24
**Status:** Ready for planning

<domain>
## Phase Boundary

Multiple family members access shared household data with polling-based sync. Users see attribution for who created/added items, get live presence indicators, and experience seamless automatic synchronization. Conflicts are handled gracefully with user-friendly resolution.

This phase does NOT include:
- Full offline-first capability (deferred — significant complexity)
- Real-time push via WebSocket (polling is the sync mechanism)
- Advanced permissions/roles (family trust model continues)

</domain>

<decisions>
## Implementation Decisions

### Attribution Display
- Avatar + name shown for item creators/adders
- Attribution appears everywhere: recipes, shopping items, meal plan entries
- Profile pictures: Google OAuth avatar with initial letter fallback
- Design for future auth expansion: user data model should support local profile storage even when initially populated from OAuth
- Timestamps: Claude's discretion based on space constraints

### Sync Behavior
- Timed polling for automatic updates (user shouldn't have to think about it)
- Seamless updates when new data arrives — silent or subtle indicator, NO toast notifications
- Small sync status indicator somewhere in UI (exact placement TBD, experiment)
- Error messages friendly for non-technical users — should not feel "broken"
- On sync failure: allow continued use of last-fetched data, queue changes optimistically
- Full offline-first with conflict resolution on reconnect: deferred to future phase

### Conflict Handling
- Strategy: Merge when possible, alert on true conflicts
- Shopping list check-offs: "Checked wins" (if either user checked it, stays checked)
- True conflicts (incompatible edits): Show in-line indicator on conflicted item, resolve when convenient
- Conflict display: Show who made the other change ("Sarah changed this to 2 lbs")
- Resolution options: Pick mine, pick theirs, edit to new value, or delete (edit/pick are primary)
- Delete conflicts (user A deletes, user B edits): Alert user B, offer to restore
- Change history tracking: Claude's discretion on appropriate level

### User Presence
- Show online status (green dot or similar) for currently active users
- Display presence both globally (header/nav) AND per-page (who's viewing current list/plan)
- Page-specific: Show cursor/focus position like Google Docs (where others are looking)
- Away/offline timeout: Claude's discretion

### Claude's Discretion
- Timestamp display format and placement
- Exact polling interval
- Subtle update indicator design
- Sync status indicator placement (header, footer, or elsewhere)
- Change history depth (simple vs detailed tracking)
- Online/away timeout duration
- Cursor indicator styling

</decisions>

<specifics>
## Specific Ideas

- "Experience should be extremely non-thought-based — just automatic and slick"
- "Users don't have technical knowledge — errors need to feel not broken even when something is broken"
- "Always default to making it easy for the user to do whatever they want"
- Cursor position presence like Google Docs for collaborative awareness
- Profile data model ready for future OAuth provider expansion

</specifics>

<deferred>
## Deferred Ideas

- **Full offline-first capability** — Work offline with eventual sync when connection returns. User wanted this but acknowledged complexity. Separate phase for robust offline support with proper conflict resolution on reconnect.
- **Multiple OAuth providers** — Currently Google-only, but user wants future support for additional authentication methods with internal user database

</deferred>

---

*Phase: 05-multi-user-collaboration*
*Context gathered: 2026-01-24*
