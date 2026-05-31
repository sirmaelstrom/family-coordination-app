<script lang="ts">
  import type { ShellContext, ChoreBoardDto } from './lib/types';
  import { getBoard, ApiError } from './lib/api';

  // Thin scaffold shell (WP-09). Proves the build → mount → fetch pipeline
  // end-to-end: reads the host context, loads the board, and renders trivial
  // counts. NO feature UI — the board grid is WP-10, mutations WP-11, lenses
  // WP-12. All dueness/decay is SERVER-computed (M5/M6); this shell never
  // derives dueness client-side.

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  let board = $state<ChoreBoardDto | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);

  async function loadBoard() {
    try {
      loading = true;
      error = null;
      board = await getBoard();
    } catch (e) {
      if (e instanceof ApiError) {
        error = `Failed to load chore board (HTTP ${e.status}).`;
      } else {
        error = e instanceof Error ? e.message : String(e);
      }
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    loadBoard();
  });
</script>

<div class="ch-container">
  <header class="ch-header">
    <h1 class="ch-title">Chores</h1>
    <span class="ch-user">{ctx.userName}</span>
  </header>

  {#if error}
    <div class="ch-inline-error" role="alert">
      <span>{error}</span>
      <button type="button" class="ch-retry" onclick={loadBoard}>Retry</button>
    </div>
  {/if}

  {#if loading && !board}
    <div class="ch-loading">Loading…</div>
  {:else if board}
    <!-- Placeholder scaffold readout. The real board UI lands in WP-10. -->
    <p class="ch-placeholder">
      Chore board loaded — {board.chores.length} chores across
      {board.rooms.length} rooms, {board.members.length} members.
      {board.needsAttentionChoreIds.length} need attention.
    </p>
  {:else if !loading}
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
    margin-bottom: 16px;
  }
  .ch-title {
    margin: 0;
    font-size: 1.5rem;
    font-weight: 500;
  }
  .ch-user {
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }
  .ch-loading,
  .ch-empty,
  .ch-placeholder {
    padding: 24px 0;
    color: var(--color-text-muted);
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
