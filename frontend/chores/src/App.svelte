<script lang="ts">
  import type { ChoreDto, ShellContext } from './lib/types';
  import { getBoard, uploadChorePhoto, ApiError } from './lib/api';
  import { boardStore } from './lib/state.svelte';
  import { startLiveness, type LivenessHandle } from './lib/liveness';
  import { showToast } from './lib/toasts.svelte';
  import ViewSwitcher from './lib/components/ViewSwitcher.svelte';
  import NeedsAttentionBoard from './lib/components/NeedsAttentionBoard.svelte';
  import QuickAddSheet, { type QuickAddValue } from './lib/components/QuickAddSheet.svelte';
  import HandOffPicker from './lib/components/HandOffPicker.svelte';
  import Toasts from './lib/components/Toasts.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // Root of the chores island. Fetches the ONE board payload into the shared
  // store, then renders the lens switcher + the default Needs-attention board.
  // Every lens is a CLIENT-SIDE grouping of that one payload (M11) — switching
  // lenses never refetches. All dueness/decay is SERVER-computed (M5/M6): this
  // component never derives dueness and never builds a Date from 'YYYY-MM-DD'.
  //
  // WP-11 wires mutation handlers (the ChoreCard action shells); WP-12 builds
  // the Rooms / Up-for-grabs / Mine lens UIs (the groupings already exist in
  // the store) + the roaming default-view persistence.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  const store = boardStore;

  let liveness: LivenessHandle | null = null;

  // ── Quick-add + hand-off dialog state ─────────────────────────────────────
  let quickAddOpen = $state(false);
  let quickAddSubmitting = $state(false);
  let handOffOpen = $state(false);
  let handOffChore = $state<ChoreDto | null>(null);

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
  function handleComplete(chore: ChoreDto) {
    store.complete(chore.id);
  }
  function handleHandOff(chore: ChoreDto) {
    handOffChore = chore;
    handOffOpen = true;
  }
  function handleHandOffSelect(targetUserId: number | null) {
    const chore = handOffChore;
    handOffOpen = false;
    handOffChore = null;
    if (chore) store.handOff(chore.id, targetUserId);
  }

  // ── Quick-add (create → optional two-step photo upload, council C2) ───────
  async function handleQuickAdd(value: QuickAddValue) {
    quickAddSubmitting = true;
    try {
      // 1. POST the create JSON (no file in the body).
      const created = await store.create(value.request);
      // 2. If a photo was chosen, upload it to the dedicated /photo route, then
      //    refetch so the board reflects the stored photoPath.
      if (value.photo) {
        try {
          await uploadChorePhoto(created.id, value.photo);
          await loadBoard();
        } catch {
          // The chore exists; only the photo failed. Don't block the create.
          showToast({ message: "Chore added, but the photo didn't upload.", kind: 'info' });
        }
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
    // Seed the viewing user's id once mounted (drives the Mine lens + "You").
    store.currentUserId = ctx.userId;
    // Wire the board refetch so the mutation layer can reconcile after a 409 /
    // other 4xx (shares the SAME loader as liveness).
    store.setRefresh(loadBoard);
    loadBoard();
    // Liveness: ~20s poll while visible + immediate refetch on refocus; pauses
    // while hidden. NOT Blazor DataNotifier/PollingService (MN2).
    liveness = startLiveness(loadBoard);
    return () => {
      liveness?.stop();
      liveness = null;
    };
  });
</script>

<div class="ch-container">
  <header class="ch-header">
    <h1 class="ch-title">Chores</h1>
    <span class="ch-user">{ctx.userName}</span>
  </header>

  <div class="ch-toolbar">
    <ViewSwitcher active={store.lens} onSelect={(l) => store.setLens(l)} />
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
    {#if store.lens === 'needs-attention'}
      <NeedsAttentionBoard
        sections={store.needsAttentionSections}
        filter={store.attentionFilter}
        onFilter={(f) => store.setAttentionFilter(f)}
        currentUserId={store.currentUserId}
        totalChores={store.needsAttentionChores.length}
        isPending={(id) => store.isPending(id)}
        onClaim={handleClaim}
        onDrop={handleDrop}
        onComplete={handleComplete}
        onHandOff={handleHandOff}
      />
    {:else}
      <!--
        WP-12 builds the Rooms / Up-for-grabs / Mine lens UIs. The groupings
        are already computed in the store (roomGroups / upForGrabsChores /
        mineChores) off the SAME board payload — no refetch on switch (M11).
        This placeholder is the seam WP-12 replaces.
      -->
      <div class="ch-lens-soon">
        <p class="ch-lens-soon-head">This view is coming soon.</p>
        <p>Switch back to <strong>Needs attention</strong> to see the board.</p>
      </div>
    {/if}
  {:else if !store.loading}
    <div class="ch-empty">No chore board data.</div>
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
  excludeUserId={handOffChore?.assigneeUserId ?? null}
  onClose={() => {
    handOffOpen = false;
    handOffChore = null;
  }}
  onSelect={handleHandOffSelect}
/>

<Toasts />

<style>
  .ch-container {
    max-width: 960px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .ch-header {
    display: flex;
    align-items: baseline;
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
  .ch-user {
    color: var(--color-text-muted);
    font-size: 0.875rem;
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
  .ch-lens-soon {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-lens-soon-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
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
