<script lang="ts">
  import type { ShellContext } from './lib/types';
  import { getBoard, ApiError } from './lib/api';
  import { boardStore } from './lib/state.svelte';
  import { startLiveness, type LivenessHandle } from './lib/liveness';
  import ViewSwitcher from './lib/components/ViewSwitcher.svelte';
  import NeedsAttentionBoard from './lib/components/NeedsAttentionBoard.svelte';

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

  $effect(() => {
    // Seed the viewing user's id once mounted (drives the Mine lens + "You").
    store.currentUserId = ctx.userId;
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
</style>
