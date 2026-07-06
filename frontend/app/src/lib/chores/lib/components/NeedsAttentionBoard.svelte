<script lang="ts">
  import type { NeedsAttentionSection } from '../state.svelte';
  import type { ChoreDto, ChoreLensId } from '../types';
  import ChoreCard from './ChoreCard.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // The unified board (Phase 14 — Model A board IA). It ALWAYS sections by
  // attention (Falling behind → Due now → Coming up, dirtiest-first). The active
  // PRIMARY FILTER (Up for grabs / Mine / All) decides WHICH chore set is
  // sectioned — that selection happens upstream in the store (`boardSections`),
  // so this component just renders whatever sections it's handed.
  //
  // ⚠ M11: the sections are a CLIENT-SIDE grouping of the ONE board payload — no
  // fetch on filter switch. ⚠ M5/M6: ordering + bucketing read the SERVER
  // `dueState`; no client dueness recompute.
  //
  // `filterLens` is used ONLY to pick the empty-state copy for the active filter.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    sections: NeedsAttentionSection[];
    /** The active primary filter — drives the empty-state copy only. */
    filterLens: ChoreLensId;
    currentUserId: number;
    /** Total chores in the active filter's set BEFORE the section split (empty state). */
    totalChores: number;
    /** True while a mutation for the given chore id is in flight. */
    isPending: (choreId: number) => boolean;
    /**
     * Phase 15 R4′ (WP-03) — the up-for-grabs "Fits me" filter is actively narrowing the pile. When true the
     * board appends the provenance note, and (if any chores were hidden) the "N bigger lifts set aside" row.
     * Defaults false so every other lens/board renders exactly as before.
     */
    fitsMeActive?: boolean;
    /** Count of pile chores hidden by the active Fits-me filter (drives the set-aside row). */
    setAsideCount?: number;
    /** "Show them" — reveal the full pile (the store clears the Fits-me filter). */
    onShowSetAside?: () => void;
    onClaim: (chore: ChoreDto) => void;
    onDrop: (chore: ChoreDto) => void;
    onComplete: (chore: ChoreDto) => void;
    onHandOff: (chore: ChoreDto) => void;
    onTake: (chore: ChoreDto) => void;
    onAssign: (chore: ChoreDto) => void;
    onCommit: (chore: ChoreDto) => void;
    onLeave: (chore: ChoreDto) => void;
    onEdit: (chore: ChoreDto) => void;
    onSnooze: (chore: ChoreDto, request: { days?: number; until?: string | null }) => void;
  }

  let {
    sections,
    filterLens,
    currentUserId,
    totalChores,
    isPending,
    fitsMeActive = false,
    setAsideCount = 0,
    onShowSetAside,
    onClaim,
    onDrop,
    onComplete,
    onHandOff,
    onTake,
    onAssign,
    onCommit,
    onLeave,
    onEdit,
    onSnooze,
  }: Props = $props();

  // The active filter's set is empty when its pre-section count is 0 (equivalent
  // to every section being empty). Drive the empty state off `totalChores` so the
  // count the store computed is the single source for "is the board empty".
  let hasVisible = $derived(totalChores > 0);

  // Empty-state copy per active primary filter.
  const EMPTY_COPY: Record<ChoreLensId, { head: string; body: string }> = {
    'up-for-grabs': { head: 'Nothing up for grabs.', body: 'Every chore has someone on it. Nice.' },
    mine: { head: 'Nothing on your plate.', body: "You're not holding any chores right now." },
    'needs-attention': { head: 'All clear.', body: 'No active chores right now.' },
    // The organizers (rooms/equity/recap) never render this board, but the map must be total.
    rooms: { head: 'All clear.', body: 'No active chores right now.' },
    equity: { head: 'All clear.', body: 'No active chores right now.' },
    recap: { head: 'All clear.', body: 'No active chores right now.' },
  };
  let emptyCopy = $derived(EMPTY_COPY[filterLens] ?? EMPTY_COPY['needs-attention']);
</script>

<div class="ch-board">
  {#if hasVisible}
    {#each sections as section (section.id)}
      <section class="ch-section">
        <header class="ch-section-head">
          <h2 class="ch-section-title">{section.title}</h2>
          <span class="ch-section-count">{section.chores.length}</span>
        </header>
        <div class="ch-section-cards">
          {#each section.chores as chore (chore.id)}
            <ChoreCard
              {chore}
              {currentUserId}
              pending={isPending(chore.id)}
              {onClaim}
              {onDrop}
              {onComplete}
              {onHandOff}
              {onTake}
              {onAssign}
              {onCommit}
              {onLeave}
              {onEdit}
              {onSnooze}
            />
          {/each}
        </div>
      </section>
    {/each}
  {:else}
    <div class="ch-board-empty">
      <p class="ch-board-empty-head">{emptyCopy.head}</p>
      <p>{emptyCopy.body}</p>
    </div>
  {/if}

  {#if fitsMeActive}
    <!--
      Phase 15 R4′ (WP-03) — the "Fits me" escape hatch + provenance. The filter is
      never a wall: any bigger lifts it set aside stay one tap away. The subject of
      every string here is the CHORE ("bigger lifts") and the viewer's OWN setting —
      never "you can't do this" (VD.4). Rendered only while the filter is active.
    -->
    {#if setAsideCount > 0}
      <div class="ch-set-aside">
        <span>{setAsideCount} bigger lift{setAsideCount === 1 ? '' : 's'} set aside</span>
        <button type="button" class="ch-set-aside-show" onclick={() => onShowSetAside?.()}>
          Show {setAsideCount === 1 ? 'it' : 'them'}
        </button>
      </div>
    {/if}
    <p class="ch-fit-note">
      Matched to each chore’s weight and your own capacity setting — never to history.
    </p>
  {/if}
</div>

<style>
  .ch-board {
    display: flex;
    flex-direction: column;
    gap: 20px;
  }

  .ch-section {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .ch-section-head {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ch-section-title {
    margin: 0;
    font-size: 0.9375rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  .ch-section-count {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 22px;
    height: 22px;
    padding: 0 7px;
    border-radius: 11px;
    background: var(--color-action-hover);
    font-size: 0.75rem;
    font-weight: 600;
    color: var(--color-text-muted);
  }
  .ch-section-cards {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  .ch-board-empty {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-board-empty-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
  }

  /* ── Fits-me escape hatch + provenance (R4′ WP-03) ──────────────────────── */
  .ch-set-aside {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
    padding: 10px 14px;
    border: 1px dashed var(--color-line-strong);
    border-radius: var(--radius-md);
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-set-aside-show {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-primary);
    background: transparent;
    border: none;
    padding: 4px 6px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    white-space: nowrap;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-set-aside-show:hover {
    background: var(--color-action-hover);
  }
  .ch-set-aside-show:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }
  .ch-fit-note {
    margin: 0;
    padding: 4px 0 0;
    font-size: 0.75rem;
    color: var(--color-text-muted);
    text-align: center;
  }
</style>
