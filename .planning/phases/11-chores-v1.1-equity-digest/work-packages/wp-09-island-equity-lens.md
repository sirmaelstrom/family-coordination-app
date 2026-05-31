# WP-09: Island Equity lens (headline frontend)

**Wave:** 7 · **Execution:** review-needed *(primary new UX)* · **Depends on:** WP-02, WP-06

## Precondition
The `ChoreEquityDto` is frozen (WP-02) + served at `GET /api/chores/equity` (WP-06). Island conventions:
`lib/state.svelte.ts` (`boardStore`), `lib/api.ts` (typed fns + `ApiError`), `lib/types.ts` (unions +
`CHORE_LENSES`), `ViewSwitcher.svelte`, `App.svelte` lens routing. CORRECTIONS: export the store instance,
never a reassigned `$state` (MN13); no `new Date('YYYY-MM-DD')` (MN9).

## Goal
Add a first-class **Equity** lens: types, a typed `getEquity` client, equity state, a visualized
`EquityBoard.svelte` (neutral per-member distribution + equal-share reference + falling-behind/up-for-grabs
context), and ViewSwitcher/App routing + the roaming default-view allowlist.

## Files
- **Modify** `frontend/chores/src/lib/types.ts` (add `'equity'` to `ChoreLensId`/`CHORE_LENSES`; add
  `ChoreEquityDto`/`MemberShareDto` mirroring `equity.json`)
- **Modify** `frontend/chores/src/lib/api.ts` (`getEquity(window): Promise<ChoreEquityDto>`)
- **Modify** `frontend/chores/src/lib/state.svelte.ts` (equity `$state` + a `loadEquity()` fetch-once-on-open)
- **Create** `frontend/chores/src/lib/components/EquityBoard.svelte`
- **Modify** `frontend/chores/src/lib/components/ViewSwitcher.svelte` — auto-discovers lenses via
  `CHORE_LENSES`, **but** add the `'equity'` entry to its exhaustive `LENS_LABEL: Record<ChoreLensId,string>`
  (and any sibling exhaustive map, e.g. a `LENS_ICON`/icon record). Adding `'equity'` to `ChoreLensId` without
  the label entry is a `svelte-check` type error (P1 from review).
- **Modify** `frontend/chores/src/App.svelte` (route `lens === 'equity'` → `<EquityBoard>`)

## Implementation notes
- Add canonical id `'equity'` to `ChoreLensId` + `CHORE_LENSES` (in lockstep with the backend `ChoreLens`
  constant — M16; WP-06 must have added it to the `/me/default-view` allowlist).
- `getEquity(window: 'week'|'all' = 'week')` → `GET /api/chores/equity?window=...` (credentials include).
- Store: `equity = $state<ChoreEquityDto|null>`, `equityWindow = $state<'week'|'all'>('week')`,
  `equityLoading`/`equityError`, and an `equityLoaded` flag. The four v1.0 lenses still group the one board
  payload (M11) — equity is the only fetcher. Export the **instance** (MN13).
- **Load mechanism (council minor — tie it to a real trigger, not a vague "on first open"):** in `App.svelte`
  add a `$effect` that, when `store.lens === 'equity'` and `!equityLoaded` (or `equityWindow` changed), calls
  `loadEquity()`. Don't rely on `setLens` being async. (`coerceLens`/`initLensFromDefault` already accept
  `'equity'` once it's in `CHORE_LENSES`, so a user defaulted onto Equity loads it on mount via this effect.)
- **Equity cache invalidation (council MAJOR — equity is a separate cached payload that goes stale):** any
  action that changes completions or chores must invalidate the cached equity so it reloads when next viewed.
  In the store, after `complete(...)` succeeds and inside the board-refetch path (`setRefresh`/`loadBoard`),
  set `equityLoaded = false` (and reload immediately if the equity lens is currently active). WP-10's
  `seedStarter()` does the same. Otherwise completing a chore leaves a stale distribution on screen.
- `EquityBoard.svelte`: render the distribution as **proportional bars** (share %), each member row with
  avatar (`MemberAvatar`) + points + share; the bar width and the printed value use `sharePct` **directly as a
  percent** (`width: {sharePct}%`, label `{sharePct}%`) — WP-02 made it 0..100, so **no client `* 100`**.
  Draw the neutral **equal-share reference** line at `equalSharePct`. A `week`/`all` toggle (switching
  `equityWindow` triggers a reload via the effect). A small "falling behind: N · up for grabs: M" context row
  read from the equity DTO's `fallingBehindCount`/`upForGrabsCount` (WP-02 added these — read them straight off
  the equity payload; do NOT cross-reference the board payload or add fields to the board DTO — M7/MN4).
  **Neutral framing** — no "winner/loser", no "behind" labels, no ranking medals (M12/MN8). Render server
  values only; **no client date math** (MN9).
- **WP-12 held-count strip disposition (council — was left undecided):** **leave `MineView.svelte`'s existing
  held-count strip untouched** in v1.1. The Equity lens is the rich superseding view; removing the small Mine
  strip is optional polish, deliberately deferred to keep this WP's boundary to the equity-lens files. (So
  `MineView.svelte` is NOT in this WP's file list — by decision, not omission.)

## Verification (V8 island portion, V14, GAP-2)
- `npm ci && npm run build` + `npx svelte-check` = 0 errors/0 warnings. `dotnet build` copies the bundle.
  Shopping-list island diff EMPTY (MN6). **Manual (owed):** with `CHORES_USE_ISLAND=true` + the dev seed,
  the Equity lens shows a real multi-member distribution; window toggle works; no network call on switching to
  the four v1.0 lenses (only equity fetches).

## Failure criteria
- Exporting a reassigned `$state` (MN13). · `new Date('YYYY-MM-DD')` or any client dueness/date recompute
  (MN9). · Ranking/leaderboard/"behind" framing (M12). · A per-lens refetch for the v1.0 lenses (M11). ·
  `types.ts` drift from `equity.json` (M7). · Touching the shopping-list island (MN6).

## Boundary
The equity lens island files ONLY. No backend change. No edit dialog / settings surface (WP-10/11). Sequential
with WP-10/11 (same island files) — this WP lands first.

## Notes for downstream
- WP-10 adds the edit dialog + backfill action to the same island; WP-11 adds the settings surface. The
  `getEquity` fetch-once pattern + the equity store fields are the seam they build alongside.
