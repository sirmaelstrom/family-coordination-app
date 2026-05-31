<script lang="ts">
  import type { AttentionFilter, NeedsAttentionSection } from '../state.svelte';
  import type { ChoreDto } from '../types';
  import ChoreCard from './ChoreCard.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // The default lens (S1/S2): Needs-attention, split Falling behind → Due now
  // → Coming up, dirtiest-first. Inclusion + ordering come from the SERVER
  // (`needsAttentionChoreIds`) via the store — no client-side dueness (M5/M11).
  //
  // Filter chips (Everything / Up-for-grabs / Mine) narrow the visible set;
  // they re-group the SAME payload (no fetch).
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    sections: NeedsAttentionSection[];
    /** Active filter chip. */
    filter: AttentionFilter;
    onFilter: (f: AttentionFilter) => void;
    currentUserId: number;
    /** Total needs-attention count BEFORE the section split (for the empty state). */
    totalChores: number;
    /** True while a mutation for the given chore id is in flight. */
    isPending: (choreId: number) => boolean;
    onClaim: (chore: ChoreDto) => void;
    onDrop: (chore: ChoreDto) => void;
    onComplete: (chore: ChoreDto) => void;
    onHandOff: (chore: ChoreDto) => void;
    onEdit: (chore: ChoreDto) => void;
  }

  let {
    sections,
    filter,
    onFilter,
    currentUserId,
    totalChores,
    isPending,
    onClaim,
    onDrop,
    onComplete,
    onHandOff,
    onEdit,
  }: Props = $props();

  const FILTERS: { id: AttentionFilter; label: string }[] = [
    { id: 'everything', label: 'Everything' },
    { id: 'up-for-grabs', label: 'Up for grabs' },
    { id: 'mine', label: 'Mine' },
  ];

  let hasVisible = $derived(sections.some((s) => s.chores.length > 0));
</script>

<div class="ch-board">
  <div class="ch-chips" role="group" aria-label="Filter chores">
    {#each FILTERS as chip (chip.id)}
      <button
        type="button"
        class="ch-chip"
        class:active={chip.id === filter}
        aria-pressed={chip.id === filter}
        onclick={() => onFilter(chip.id)}
      >
        {chip.label}
      </button>
    {/each}
  </div>

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
              {onEdit}
            />
          {/each}
        </div>
      </section>
    {/each}
  {:else}
    <div class="ch-board-empty">
      {#if totalChores === 0 && filter === 'everything'}
        <p class="ch-board-empty-head">All clear.</p>
        <p>Nothing needs attention right now — nice work.</p>
      {:else}
        <p class="ch-board-empty-head">Nothing here.</p>
        <p>No chores match this filter.</p>
      {/if}
    </div>
  {/if}
</div>

<style>
  .ch-board {
    display: flex;
    flex-direction: column;
    gap: 20px;
  }

  .ch-chips {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  .ch-chip {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    padding: 6px 14px;
    min-height: 32px;
    border-radius: 16px;
    cursor: pointer;
    transition:
      background-color 0.15s,
      color 0.15s,
      border-color 0.15s;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-chip:hover:not(.active) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .ch-chip.active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: #fff;
  }
  .ch-chip:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
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
</style>
