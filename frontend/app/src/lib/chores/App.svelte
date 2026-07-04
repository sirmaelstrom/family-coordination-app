<script lang="ts">
  import { untrack } from 'svelte';
  import type { ChoreDto, ShellContext } from './lib/types';
  import { getBoard, uploadChorePhoto, createSubtask, ApiError } from './lib/api';
  import { boardStore } from './lib/state.svelte';
  import { startLiveness, type LivenessHandle } from './lib/liveness';
  import { showToast } from '$lib/shared/toast-store.svelte';
  import ViewSwitcher from './lib/components/ViewSwitcher.svelte';
  import NeedsAttentionBoard from './lib/components/NeedsAttentionBoard.svelte';
  import RoomsDashboard from './lib/components/RoomsDashboard.svelte';
  import EquityBoard from './lib/components/EquityBoard.svelte';
  import RecapBoard from './lib/components/RecapBoard.svelte';
  import QuickAddSheet, { type QuickAddValue } from './lib/components/QuickAddSheet.svelte';
  import EditChoreSheet from './lib/components/EditChoreSheet.svelte';
  import HandOffPicker from './lib/components/HandOffPicker.svelte';
  import CompleteDialog from './lib/components/CompleteDialog.svelte';
  import DigestSettings from './lib/components/DigestSettings.svelte';
  import PhotoLightbox from './lib/components/PhotoLightbox.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // Root of the chores island. Fetches the ONE board payload into the shared
  // store, then renders the view control + the active view. Model A board IA
  // (Phase 14): a single attention-sectioned board with a PRIMARY filter
  // (Up for grabs / Mine / All — lens ids up-for-grabs / mine / needs-attention)
  // plus two on-demand ORGANIZERS (Rooms / Equity). Every view is a CLIENT-SIDE
  // grouping of that one payload (M11) — switching never refetches. All
  // dueness/decay is SERVER-computed (M5/M6): this component never derives
  // dueness and never builds a Date from 'YYYY-MM-DD'.
  //
  // WP-11 wired the mutation handlers (shared by every view via ChoreCard).
  // The roaming per-user default view: the store opens onto `board.userDefaultView`
  // on first load (null ⇒ Up for grabs) and persists changes via PATCH
  // /api/chores/me/default-view (server-stored on User.ChoresDefaultView so it
  // roams across devices — NOT localStorage).
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  const store = boardStore;

  let liveness: LivenessHandle | null = null;

  // ── Quick-add + hand-off + edit + digest-settings dialog state ───────────
  let quickAddOpen = $state(false);
  let quickAddSubmitting = $state(false);
  let handOffOpen = $state(false);
  let handOffChore = $state<ChoreDto | null>(null);
  // The shared member picker serves both "hand off / reassign" (held chores) and
  // "assign" (up-for-grabs chores). The mode only changes copy + whether the
  // "Leave for anyone" pile row shows — both call store.handOff on select.
  let pickerMode = $state<'handoff' | 'assign'>('handoff');
  let completeOpen = $state(false);
  let completeChore = $state<ChoreDto | null>(null);
  let editOpen = $state(false);
  let editChore = $state<ChoreDto | null>(null);
  let digestSettingsOpen = $state(false);

  async function loadBoard() {
    try {
      // Keep the spinner only for the FIRST load; liveness refreshes are silent.
      if (store.board == null) store.loading = true;
      store.error = null;
      store.setBoard(await getBoard());
    } catch (e) {
      if (e instanceof ApiError) {
        store.error = `Failed to load the chore board (HTTP ${e.status}).`;
      } else {
        store.error = e instanceof Error ? e.message : String(e);
      }
    } finally {
      store.loading = false;
    }
  }

  // ── Card action handlers (optimistic mutations live in the store) ─────────
  // The store owns the optimistic update + xmin-409 reconciliation; these are
  // thin pass-throughs so any lens (WP-12) can reuse the same handlers.
  function handleClaim(chore: ChoreDto) {
    store.claim(chore.id);
  }
  function handleDrop(chore: ChoreDto) {
    store.drop(chore.id);
  }
  function handleCommit(chore: ChoreDto) {
    store.commit(chore.id);
  }
  function handleLeave(chore: ChoreDto) {
    store.leave(chore.id);
  }
  function handleComplete(chore: ChoreDto) {
    // Multi-person (co-sign) chore → open the participant dialog so the present
    // member can record who's marking it done (D7). Single-person chore → keep
    // the exact one-tap Done (unchanged): complete immediately, no dialog.
    if (chore.requiredCount > 1) {
      completeChore = chore;
      completeOpen = true;
    } else {
      store.complete(chore.id);
    }
  }
  function handleCompleteSubmit(participantUserIds: number[]) {
    const chore = completeChore;
    completeOpen = false;
    completeChore = null;
    if (chore) store.complete(chore.id, { participantUserIds });
  }
  function handleHandOff(chore: ChoreDto) {
    pickerMode = 'handoff';
    handOffChore = chore;
    handOffOpen = true;
  }
  /**
   * Assign an up-for-grabs chore to a chosen member. Opens the SAME member picker
   * in "assign" mode; selecting a member runs store.handOff (a member target lands
   * a deliberate Assigned server-side — see ChoreService.HandOffAsync). No new
   * endpoint: assigning from the pile is just a hand-off with no current holder.
   */
  function handleAssign(chore: ChoreDto) {
    pickerMode = 'assign';
    handOffChore = chore;
    handOffOpen = true;
  }
  function handleHandOffSelect(targetUserId: number | null) {
    const chore = handOffChore;
    handOffOpen = false;
    handOffChore = null;
    if (chore) store.handOff(chore.id, targetUserId);
  }
  function handleTake(chore: ChoreDto) {
    // Take a chore currently held by someone else — grab it as a self-claim in
    // one tap (covering for someone out/sick). No coordination with the holder
    // needed; lands a Claimed (not a sticky assignment), so a recurring chore
    // returns to the pile after this user completes it.
    store.take(chore.id);
  }

  /** Open the edit sheet for a chore. */
  function handleEdit(chore: ChoreDto) {
    editChore = chore;
    editOpen = true;
  }

  /**
   * Snooze / un-snooze a chore (board quick-snooze). Thin pass-through to the
   * store's optimistic snooze; the server resolves the floor date in the household
   * timezone (MN4). `request.days` = N days from today; `request.until` = explicit
   * "YYYY-MM-DD"; both omitted ⇒ un-snooze.
   */
  function handleSnooze(chore: ChoreDto, request: { days?: number; until?: string | null }) {
    store.snooze(chore.id, request);
  }

  /** Seed the starter set (shown only when the board is empty). */
  async function handleSeedStarter() {
    await store.seedStarter();
  }

  // ── Quick-add (create → optional two-step photo upload, council C2) ───────
  async function handleQuickAdd(value: QuickAddValue) {
    quickAddSubmitting = true;
    try {
      // 1. POST the create JSON (no file in the body).
      const created = await store.create(value.request);
      // 2. Checklist items: a new chore has no id until now, so create each item
      //    against the returned id (versionless). One bad item shouldn't block.
      for (const title of value.subtasks) {
        try {
          await createSubtask(created.id, { title });
        } catch {
          // Skip a failed item; the chore + the rest still land.
        }
      }
      // 3. If a photo was chosen, upload it to the dedicated /photo route.
      if (value.photo) {
        try {
          await uploadChorePhoto(created.id, value.photo);
        } catch {
          // The chore exists; only the photo failed. Don't block the create.
          showToast({ message: "Chore added, but the photo didn't upload.", kind: 'info' });
        }
      }
      // Refetch once so the new checklist + photo render (store.create reconciled
      // BEFORE these follow-on writes, so the board doesn't carry them yet).
      if (value.subtasks.length > 0 || value.photo) {
        await loadBoard();
      }
      quickAddOpen = false;
      showToast({ message: `Added “${created.name}”.`, kind: 'success' });
    } catch (e) {
      // Create rejected — keep the sheet open so the user can fix + retry.
      if (e instanceof ApiError && e.isClientRejection) {
        showToast({ message: "That chore couldn't be added — check the details.", kind: 'error' });
      } else {
        showToast({ message: 'Something went wrong adding the chore.', kind: 'error' });
      }
    } finally {
      quickAddSubmitting = false;
    }
  }

  $effect(() => {
    // One-time mount setup. MUST run exactly once — `loadBoard()` synchronously
    // reads `store.board` (the spinner-on-first-load check) and its async
    // `setBoard` REWRITES it, so WITHOUT untrack the effect would subscribe to the
    // very state it updates → an infinite fetch→setBoard→re-run loop (~tens of
    // req/s). `untrack` gives the body no reactive deps; liveness drives refreshes.
    untrack(() => {
      // Seed the viewing user's id once mounted (drives the Mine lens + "You").
      store.currentUserId = ctx.userId;
      // Wire the board refetch so the mutation layer can reconcile after a 409 /
      // other 4xx (shares the SAME loader as liveness).
      store.setRefresh(loadBoard);
      loadBoard();
      // Liveness: ~20s poll while visible + immediate refetch on refocus; pauses
      // while hidden. NOT Blazor DataNotifier/PollingService (MN2).
      liveness = startLiveness(loadBoard);
    });
    return () => {
      liveness?.stop();
      liveness = null;
    };
  });

  // ── Equity fetch-on-open (the ONLY non-board fetcher — M11) ───────────────
  // Load the equity payload when the Equity lens is open and the cache is stale
  // (`!equityLoaded`). Reading `equityWindow` makes the effect re-run on a window
  // switch (which the store also marks stale via setEquityWindow). The four v1.0
  // lenses never trigger a fetch — they group the one board payload. A user who
  // defaulted onto Equity lands here on mount (the store opens onto their default
  // lens) and loads it the same way. The store guards re-entrancy + window races.
  $effect(() => {
    // Track the window so a switch re-runs this effect.
    const _window = store.equityWindow;
    void _window;
    // Guard on `!equityError` so a FAILED load does not auto-retry forever: on error
    // `equityLoaded` stays false, so without this the effect would re-fire the moment
    // `equityLoading` clears. The explicit Retry (and a window switch) clears the error.
    if (
      store.lens === 'equity' &&
      !store.equityLoaded &&
      !store.equityLoading &&
      !store.equityError
    ) {
      store.loadEquity();
    }
  });

  // ── Recap fetch-on-open (a second cached fetcher, like equity — M11) ──────
  // Load the recap payload when the Recap lens is open and the cache is stale.
  // A user who defaulted onto Recap lands here on mount and loads it the same way.
  // The store guards re-entrancy; invalidation (completions/snooze/edit) reloads it.
  // `!recapError` guard: don't auto-retry a failed load in a loop — the Retry button
  // (which clears the error) is the re-attempt path.
  $effect(() => {
    if (
      store.lens === 'recap' &&
      !store.recapLoaded &&
      !store.recapLoading &&
      !store.recapError
    ) {
      store.loadRecap();
    }
  });
</script>

<div class="ch-container">
  <header class="ch-header">
    <h1 class="ch-title">Chores</h1>
    <div class="ch-header-end">
      <span class="ch-user">{ctx.userName}</span>
      <button
        type="button"
        class="ch-settings-btn"
        aria-label="Digest settings"
        onclick={() => (digestSettingsOpen = true)}
      >
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path
            d="M19.14 12.94c.04-.3.06-.61.06-.94s-.02-.64-.07-.94l2.03-1.58a.49.49 0 0 0 .12-.61l-1.92-3.32a.49.49 0 0 0-.59-.22l-2.39.96a7.02 7.02 0 0 0-1.62-.94l-.36-2.54a.484.484 0 0 0-.48-.41h-3.84c-.24 0-.43.17-.47.41l-.36 2.54a7.02 7.02 0 0 0-1.62.94l-2.39-.96a.48.48 0 0 0-.59.22L2.74 8.87a.47.47 0 0 0 .12.61l2.03 1.58c-.05.3-.08.62-.08.94s.03.64.07.94L2.75 14.52a.49.49 0 0 0-.12.61l1.92 3.32c.12.22.37.29.59.22l2.39-.96c.5.36 1.04.67 1.62.94l.36 2.54c.05.24.24.41.48.41h3.84c.24 0 .44-.17.47-.41l.36-2.54a7.02 7.02 0 0 0 1.62-.94l2.39.96c.22.08.47 0 .59-.22l1.92-3.32a.47.47 0 0 0-.12-.61l-2.01-1.58zM12 15.6c-1.98 0-3.6-1.62-3.6-3.6s1.62-3.6 3.6-3.6 3.6 1.62 3.6 3.6-1.62 3.6-3.6 3.6z"
            fill="currentColor"
          />
        </svg>
      </button>
    </div>
  </header>

  <div class="ch-toolbar">
    <ViewSwitcher
      active={store.lens}
      defaultLens={store.defaultView ?? 'up-for-grabs'}
      saving={store.savingDefaultView}
      onSelect={(l) => store.setLens(l)}
      onSetDefault={(l) => store.saveDefaultView(l)}
    />
  </div>

  {#if store.error}
    <div class="ch-inline-error" role="alert">
      <span>{store.error}</span>
      <button type="button" class="ch-retry" onclick={loadBoard}>Retry</button>
    </div>
  {/if}

  {#if store.loading && !store.board}
    <div class="ch-loading">Loading the board…</div>
  {:else if store.board}
    <!--
      View routing (Model A board IA). The PRIMARY filters (Up for grabs / Mine /
      All) all render the SAME unified attention-sectioned board — only the
      filtered chore set differs (store.boardSections, driven by store.lens). The
      two ORGANIZERS (Rooms / Equity) render their own surfaces. Every view is a
      CLIENT-SIDE grouping of the ONE board payload — switching `store.lens` NEVER
      refetches (M11). All share the same optimistic card handlers.
    -->
    {#if store.lens === 'up-for-grabs' || store.lens === 'mine' || store.lens === 'needs-attention'}
      <NeedsAttentionBoard
        sections={store.boardSections}
        filterLens={store.lens}
        currentUserId={store.currentUserId}
        totalChores={store.boardTotalCount}
        isPending={(id) => store.isPending(id)}
        onClaim={handleClaim}
        onDrop={handleDrop}
        onComplete={handleComplete}
        onHandOff={handleHandOff}
        onTake={handleTake}
        onAssign={handleAssign}
        onCommit={handleCommit}
        onLeave={handleLeave}
        onEdit={handleEdit}
        onSnooze={handleSnooze}
      />
    {:else if store.lens === 'rooms'}
      <RoomsDashboard
        groups={store.roomGroups}
        currentUserId={store.currentUserId}
        isPending={(id) => store.isPending(id)}
        onClaim={handleClaim}
        onDrop={handleDrop}
        onComplete={handleComplete}
        onHandOff={handleHandOff}
        onTake={handleTake}
        onAssign={handleAssign}
        onCommit={handleCommit}
        onLeave={handleLeave}
        onEdit={handleEdit}
        onSnooze={handleSnooze}
      />
    {:else if store.lens === 'equity'}
      <!--
        Equity lens — the one lens with its own (separately cached) payload
        (store.equity via GET /api/chores/equity). The $effect above fetches it
        on open + on window change; completions/refetches invalidate it. NEUTRAL
        distribution, server values only (M12/MN9).
      -->
      <EquityBoard
        equity={store.equity}
        window={store.equityWindow}
        loading={store.equityLoading}
        error={store.equityError}
        onWindow={(w) => store.setEquityWindow(w)}
        onRetry={() => store.loadEquity()}
        onCapacity={(t) => store.saveCapacity(t)}
        savingCapacity={store.savingCapacity}
      />
    {:else if store.lens === 'recap'}
      <!--
        Recap lens — the in-app weekly recap (GET /api/chores/recap). The $effect
        above fetches it on open; completions/snooze/edit invalidate it (folded into
        invalidateEquity). Shows the SAME content the Discord digest posts plus the
        week-over-week trend. Server values only; NO client date math (MN9).
      -->
      <RecapBoard
        recap={store.recap}
        loading={store.recapLoading}
        error={store.recapError}
        onRetry={() => store.loadRecap()}
      />
    {/if}
  {:else if !store.loading}
    <div class="ch-empty">No chore board data.</div>
  {/if}

  <!--
    Empty-board prompt: shown only when the board has loaded but has zero chores.
    Idempotent server-side — a second tap is a safe no-op.
  -->
  {#if store.board && store.board.chores.length === 0}
    <div class="ch-seed-prompt">
      <p class="ch-seed-text">No chores yet. Want to start with a suggested set?</p>
      <button type="button" class="ch-seed-btn" onclick={handleSeedStarter}>
        Load starter chores
      </button>
    </div>
  {/if}
</div>

<button
  type="button"
  class="ch-fab"
  aria-label="Add a chore"
  onclick={() => (quickAddOpen = true)}
  disabled={!store.board}
>
  <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
    <path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" fill="currentColor" />
  </svg>
</button>

<QuickAddSheet
  open={quickAddOpen}
  submitting={quickAddSubmitting}
  members={store.board?.members ?? []}
  rooms={store.board?.rooms ?? []}
  onClose={() => (quickAddOpen = false)}
  onSubmit={handleQuickAdd}
/>

<HandOffPicker
  open={handOffOpen}
  choreName={handOffChore?.name ?? ''}
  members={store.board?.members ?? []}
  mode={pickerMode}
  excludeUserId={pickerMode === 'assign' ? null : (handOffChore?.assigneeUserId ?? null)}
  onClose={() => {
    handOffOpen = false;
    handOffChore = null;
  }}
  onSelect={handleHandOffSelect}
/>

<CompleteDialog
  open={completeOpen}
  chore={completeChore}
  members={store.board?.members ?? []}
  currentUserId={store.currentUserId}
  onClose={() => {
    completeOpen = false;
    completeChore = null;
  }}
  onSubmit={handleCompleteSubmit}
/>

<EditChoreSheet
  open={editOpen}
  chore={editChore}
  members={store.board?.members ?? []}
  rooms={store.board?.rooms ?? []}
  onClose={() => {
    editOpen = false;
    editChore = null;
  }}
/>

<DigestSettings
  open={digestSettingsOpen}
  onClose={() => (digestSettingsOpen = false)}
/>

<!-- Single shared tap-to-enlarge overlay; opened via showPhoto() from any surface. -->
<PhotoLightbox />

<style>
  .ch-container {
    max-width: 960px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .ch-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 16px;
  }
  .ch-title {
    margin: 0;
    font-size: 2.125rem;
    font-weight: 400;
    color: var(--color-text);
  }
  .ch-header-end {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ch-user {
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }
  .ch-settings-btn {
    display: grid;
    place-items: center;
    background: transparent;
    border: none;
    cursor: pointer;
    color: var(--color-text-muted);
    width: 36px;
    height: 36px;
    border-radius: var(--radius-sm);
    padding: 0;
    transition: color 0.15s, background-color 0.15s;
  }
  .ch-settings-btn:hover {
    color: var(--color-text);
    background: var(--color-action-hover);
  }
  .ch-toolbar {
    margin-bottom: 24px;
  }
  .ch-loading,
  .ch-empty {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-seed-prompt {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 12px;
    padding: 32px 16px;
    text-align: center;
  }
  .ch-seed-text {
    margin: 0;
    color: var(--color-text-muted);
    font-size: 0.9375rem;
  }
  .ch-seed-btn {
    font: inherit;
    font-size: 0.9375rem;
    font-weight: 500;
    padding: 10px 24px;
    border: 1px solid var(--color-primary);
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-primary);
    cursor: pointer;
    min-height: 44px;
    transition: background-color 0.15s;
  }
  .ch-seed-btn:hover {
    background: var(--color-action-hover);
  }
  .ch-inline-error {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    margin-bottom: 16px;
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    border-radius: var(--radius-sm);
    color: var(--color-error);
    font-size: 0.875rem;
  }
  .ch-retry {
    font: inherit;
    border: none;
    cursor: pointer;
    border-radius: var(--radius-sm);
    padding: 4px 12px;
    background: transparent;
    color: var(--color-error);
    font-weight: 500;
  }
  .ch-retry:hover {
    background: rgba(229, 57, 53, 0.12);
  }

  .ch-fab {
    position: fixed;
    bottom: calc(24px + env(safe-area-inset-bottom, 0px));
    right: 24px;
    width: 56px;
    height: 56px;
    border-radius: 50%;
    border: none;
    background: var(--color-primary);
    color: #fff;
    display: grid;
    place-items: center;
    cursor: pointer;
    box-shadow: var(--shadow-4);
    transition:
      background-color 0.15s,
      transform 0.1s;
    z-index: 1050;
  }
  /* Mobile: clear the MainLayout bottom nav + safe-area inset. */
  @media (max-width: 960px) {
    .ch-fab {
      bottom: calc(80px + env(safe-area-inset-bottom, 0px));
    }
  }
  .ch-fab:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .ch-fab:active:not(:disabled) {
    transform: scale(0.95);
  }
  .ch-fab:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
