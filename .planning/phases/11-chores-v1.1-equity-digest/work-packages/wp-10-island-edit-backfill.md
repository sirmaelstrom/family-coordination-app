# WP-10: Island edit-chore dialog + "load starter set" action

**Wave:** 8 · **Execution:** autonomous · **Depends on:** WP-09 (same island files), WP-06 (backfill endpoint)

## Precondition
`updateChore(id, body)` → `PUT /api/chores/{id}` is already wired in `api.ts` (v1.0). The backfill endpoint
`POST /api/chores/seed-starter` exists (WP-06). The equity lens (WP-09) has landed (sequential island work).

## Goal
Two go-live table stakes: an **edit-chore dialog** that drives the existing `PUT`, and a one-tap **"Load
starter chores"** action for an empty board.

## Files
- **Create** `frontend/chores/src/lib/components/EditChoreSheet.svelte` (reuse `QuickAddSheet`'s form)
- **Modify** `frontend/chores/src/lib/components/ChoreCard.svelte` (an "Edit" affordance in the overflow; new `onEdit` prop)
- **Modify** the lens boards that render `ChoreCard` to thread the `onEdit` prop through (council C3 — the
  card's handlers are passed down per lens, so each must forward `onEdit`): `NeedsAttentionBoard.svelte`,
  `RoomsDashboard.svelte`, `UpForGrabsLane.svelte`, `MineView.svelte`. (EquityBoard renders no cards.)
- **Modify** `frontend/chores/src/lib/state.svelte.ts` (an `edit(id, UpdateChoreRequest)` optimistic method; a `seedStarter()` action; equity invalidation hook)
- **Modify** `frontend/chores/src/lib/api.ts` — `seedStarter(): Promise<{seeded:boolean}>` if not present; **add
  `photoPath?: string | null` to the frontend `UpdateChoreRequest` type** (council C4 — backend
  `UpdateChoreRequest` has `PhotoPath` but the TS type at `api.ts:~88` omits it, so editing a photo is
  impossible without this).
- **Modify** `frontend/chores/src/App.svelte` (wire the edit sheet open/save; pass `onEdit` to each lens board; an empty-board "Load starter chores" button)

## Implementation notes
- `EditChoreSheet`: pre-fill from the `ChoreDto` (name, description, room, recurrence cadence mapping,
  effort, owner, photo); on save call `store.edit(id, body)` → `updateChore(id, body)` with the card's
  `version`; 409-aware (same reconcile/refetch path as other mutations — `isConflict`→refetch+toast,
  `isClientRejection`→surface). Assignment is NOT editable here (v1.0 D6 — assignment moves via claim/handoff).
  Recurrence cadence uses the SAME 3-segment mapping as QuickAddSheet (Just once / Every N days / Specific
  day(s); D4-B — no monthly-on-day).
- **Edit-photo flow (council C4 — `UpdateChoreRequest.PhotoPath` exists server-side; wire it end-to-end):** to
  replace a chore's photo, first `uploadChorePhoto(chore.id, file)` (existing endpoint, returns `{ photoPath }`),
  then include that `photoPath` in the `updateChore` body (the newly-added `photoPath?` field). To keep an
  existing photo, send the current `photoPath` unchanged; the multipart-then-JSON two-step mirrors QuickAdd
  (C2). (If photo-edit is dropped for v1.1, state that explicitly — but the `photoPath?` type addition is still
  needed so the field round-trips.)
- `seedStarter()`: POST the backfill; on success refetch the board + **invalidate the equity cache**
  (`equityLoaded = false`, reload if the equity lens is active — WP-09 hook) + success toast ("Starter chores
  added"). Surface the "Load starter chores" button only when the board is empty (no chores).
- MN9 (no client date math), MN13 (export the store instance), M11 (no per-lens refetch for the v1.0 lenses)
  all still hold.

## Verification (V8, V9 island portion, V14)
- `npm run build` + `npx svelte-check` = 0/0. **Manual (owed):** edit a chore → board reflects it; stale
  version → reconcile toast; on an empty household, "Load starter chores" populates it; re-tap is a safe
  no-op (idempotent backfill).

## Failure criteria
- Edit dialog lets assignment change (violates v1.0 D6). · No 409 handling on save. · `new Date('YYYY-MM-DD')`
  (MN9). · Backfill button shown on a non-empty board / not idempotent UX. · Shopping-list island touched (MN6).

## Boundary
Edit dialog + backfill action island files ONLY. No backend change (PUT + seed-starter already exist). No
settings surface (WP-11). Sequential after WP-09, before WP-11.

## Notes for downstream
- WP-11 (digest-settings surface) is the last island WP; it reuses the same dialog/sheet conventions
  established here + in QuickAddSheet.
