<script lang="ts">
  import type { ChoreDto } from '../types';
  import ChoreCard from './ChoreCard.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // Up-for-grabs lens (S8) — the unclaimed pile as a single focused lane.
  // Includes stale claims, which the store already treats as pile-eligible
  // (isClaimStale — WP-04/05 stale-claim UX). The shared `ChoreCard` surfaces
  // the Claim affordance for these (it derives `isUnclaimed` from the chore).
  //
  // ⚠ M11: this is a CLIENT-SIDE grouping of the ONE board payload
  // (store.upForGrabsChores); no fetch on switch. Ordering is the store's
  // server-driven dirtiest-first.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    chores: ChoreDto[];
    currentUserId: number;
    isPending: (choreId: number) => boolean;
    onClaim: (chore: ChoreDto) => void;
    onDrop: (chore: ChoreDto) => void;
    onComplete: (chore: ChoreDto) => void;
    onHandOff: (chore: ChoreDto) => void;
    onEdit: (chore: ChoreDto) => void;
  }

  let { chores, currentUserId, isPending, onClaim, onDrop, onComplete, onHandOff, onEdit }: Props =
    $props();
</script>

<div class="ch-grabs">
  {#if chores.length > 0}
    <header class="ch-grabs-head">
      <h2 class="ch-grabs-title">Up for grabs</h2>
      <span class="ch-grabs-count">{chores.length}</span>
    </header>
    <p class="ch-grabs-hint">Anyone can pick these up — no pressure, no nagging.</p>
    <div class="ch-grabs-cards">
      {#each chores as chore (chore.id)}
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
  {:else}
    <div class="ch-grabs-empty">
      <p class="ch-grabs-empty-head">The pile is empty.</p>
      <p>Every chore has someone on it. Nice.</p>
    </div>
  {/if}
</div>

<style>
  .ch-grabs {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .ch-grabs-head {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ch-grabs-title {
    margin: 0;
    font-size: 0.9375rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  .ch-grabs-count {
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
  .ch-grabs-hint {
    margin: 0 0 8px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-grabs-cards {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .ch-grabs-empty {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-grabs-empty-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
  }
</style>
