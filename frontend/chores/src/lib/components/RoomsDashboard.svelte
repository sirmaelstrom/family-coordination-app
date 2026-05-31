<script lang="ts">
  import type { RoomGroup } from '../state.svelte';
  import type { ChoreDto, RoomRollupStatus } from '../types';
  import ChoreCard from './ChoreCard.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // Rooms lens (S7) — a rollup dashboard (D9): one card per room (incl. the
  // virtual "General" group, roomId === null) showing the room's dirtiness
  // status (clean / attention / needs-work) computed SERVER-SIDE. Tapping a
  // room drills into its chore list, rendered with the shared `ChoreCard`.
  //
  // ⚠ M5/M6/M11: the rollup `status` + `dueCount` are SERVER-computed and
  // arrive on the board payload; we never recompute dirtiness here. This is a
  // CLIENT-SIDE grouping of the ONE board payload — no fetch on drill-in.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    groups: RoomGroup[];
    currentUserId: number;
    isPending: (choreId: number) => boolean;
    onClaim: (chore: ChoreDto) => void;
    onDrop: (chore: ChoreDto) => void;
    onComplete: (chore: ChoreDto) => void;
    onHandOff: (chore: ChoreDto) => void;
    onEdit: (chore: ChoreDto) => void;
  }

  let { groups, currentUserId, isPending, onClaim, onDrop, onComplete, onHandOff, onEdit }: Props =
    $props();

  // The drilled-into room (by roomId; null = the General group; undefined = the
  // dashboard grid). Number-or-null can't disambiguate "General" from "grid", so
  // we track a separate boolean.
  let openRoomId = $state<number | null>(null);
  let drilledIn = $state(false);

  let openGroup = $derived.by<RoomGroup | null>(() => {
    if (!drilledIn) return null;
    return groups.find((g) => (g.rollup.roomId ?? null) === openRoomId) ?? null;
  });

  function openRoom(group: RoomGroup): void {
    openRoomId = group.rollup.roomId ?? null;
    drilledIn = true;
  }
  function backToGrid(): void {
    drilledIn = false;
    openRoomId = null;
  }

  // ── Server rollup status → label / accent (no client dirtiness recompute) ──
  const STATUS_LABEL: Record<RoomRollupStatus, string> = {
    clean: 'All clean',
    attention: 'Needs a look',
    needsWork: 'Needs work',
  };
</script>

{#if openGroup}
  <!-- Drill-in: a single room's chore list. -->
  <div class="ch-rooms-drill">
    <button type="button" class="ch-rooms-back" onclick={backToGrid}>
      <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
        <path d="M15 18l-6-6 6-6" fill="none" stroke="currentColor" stroke-width="2" />
      </svg>
      All rooms
    </button>

    <header class="ch-room-head">
      <span class="ch-room-icon" aria-hidden="true">{openGroup.rollup.icon}</span>
      <div class="ch-room-head-text">
        <h2 class="ch-room-name">{openGroup.rollup.name}</h2>
        <span class="ch-room-sub">
          {openGroup.rollup.choreCount}
          {openGroup.rollup.choreCount === 1 ? 'chore' : 'chores'}
          {#if openGroup.rollup.dueCount > 0}
            · {openGroup.rollup.dueCount} need attention
          {/if}
        </span>
      </div>
      <span class="ch-room-status ch-status-{openGroup.rollup.status}">
        {STATUS_LABEL[openGroup.rollup.status]}
      </span>
    </header>

    {#if openGroup.chores.length > 0}
      <div class="ch-room-cards">
        {#each openGroup.chores as chore (chore.id)}
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
      <div class="ch-rooms-empty">
        <p class="ch-rooms-empty-head">Nothing here.</p>
        <p>This room has no active chores.</p>
      </div>
    {/if}
  </div>
{:else}
  <!-- Dashboard: one rollup card per room. -->
  {#if groups.length > 0}
    <div class="ch-rooms-grid">
      {#each groups as group (group.rollup.roomId ?? 'general')}
        <button
          type="button"
          class="ch-room-card ch-status-{group.rollup.status}"
          onclick={() => openRoom(group)}
          aria-label="{group.rollup.name}: {STATUS_LABEL[group.rollup.status]}, {group.rollup
            .choreCount} chores"
        >
          <span class="ch-room-card-accent" aria-hidden="true"></span>
          <span class="ch-room-card-body">
            <span class="ch-room-card-top">
              <span class="ch-room-card-icon" aria-hidden="true">{group.rollup.icon}</span>
              <span class="ch-room-card-name">{group.rollup.name}</span>
            </span>
            <span class="ch-room-card-meta">
              <span class="ch-room-status-pill ch-status-{group.rollup.status}">
                {STATUS_LABEL[group.rollup.status]}
              </span>
              <span class="ch-room-card-counts">
                {#if group.rollup.dueCount > 0}
                  {group.rollup.dueCount} due · {group.rollup.choreCount} total
                {:else}
                  {group.rollup.choreCount}
                  {group.rollup.choreCount === 1 ? 'chore' : 'chores'}
                {/if}
              </span>
            </span>
          </span>
          <svg
            class="ch-room-card-chevron"
            viewBox="0 0 24 24"
            width="20"
            height="20"
            aria-hidden="true"
          >
            <path d="M9 6l6 6-6 6" fill="none" stroke="currentColor" stroke-width="2" />
          </svg>
        </button>
      {/each}
    </div>
  {:else}
    <div class="ch-rooms-empty">
      <p class="ch-rooms-empty-head">No rooms yet.</p>
      <p>Chores will show up here once there are rooms.</p>
    </div>
  {/if}
{/if}

<style>
  /* ── Status accent (server-computed rollup status; never derived here) ──── */
  .ch-status-clean {
    --status-accent: var(--color-success);
  }
  .ch-status-attention {
    --status-accent: var(--color-warning);
  }
  .ch-status-needsWork {
    --status-accent: var(--color-error);
  }

  /* ── Dashboard grid ─────────────────────────────────────────────────────── */
  .ch-rooms-grid {
    display: grid;
    grid-template-columns: repeat(auto-fill, minmax(220px, 1fr));
    gap: 12px;
  }
  .ch-room-card {
    display: flex;
    align-items: stretch;
    gap: 0;
    text-align: left;
    font: inherit;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    background: var(--color-surface);
    box-shadow: var(--shadow-1);
    cursor: pointer;
    overflow: hidden;
    padding: 0;
    transition:
      box-shadow 0.15s,
      transform 0.1s;
    -webkit-tap-highlight-color: transparent;
  }
  .ch-room-card:hover {
    box-shadow: var(--shadow-2);
  }
  .ch-room-card:active {
    transform: scale(0.99);
  }
  .ch-room-card:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }
  .ch-room-card-accent {
    flex-shrink: 0;
    width: 6px;
    background: var(--status-accent);
  }
  .ch-room-card-body {
    flex: 1;
    min-width: 0;
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 14px 14px 14px 12px;
  }
  .ch-room-card-top {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
  }
  .ch-room-card-icon {
    font-size: 1.5rem;
    line-height: 1;
    flex-shrink: 0;
  }
  .ch-room-card-name {
    font-size: 1.0625rem;
    font-weight: 500;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ch-room-card-meta {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .ch-room-card-counts {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-room-card-chevron {
    align-self: center;
    margin-right: 10px;
    color: var(--color-text-muted);
    flex-shrink: 0;
  }

  /* ── Status pill (dashboard + drill header) ─────────────────────────────── */
  .ch-room-status-pill,
  .ch-room-status {
    display: inline-flex;
    align-items: center;
    align-self: flex-start;
    height: 22px;
    padding: 0 10px;
    border-radius: 11px;
    font-size: 0.6875rem;
    font-weight: 600;
    letter-spacing: 0.02em;
    text-transform: uppercase;
    white-space: nowrap;
    color: var(--status-accent);
    background: transparent;
    border: 1px solid var(--status-accent);
  }
  /* Needs-work reads loudest — solid fill. */
  .ch-status-needsWork.ch-room-status-pill,
  .ch-room-status.ch-status-needsWork {
    color: #fff;
    background: var(--status-accent);
    border-color: var(--status-accent);
  }

  /* ── Drill-in ───────────────────────────────────────────────────────────── */
  .ch-rooms-drill {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }
  .ch-rooms-back {
    display: inline-flex;
    align-items: center;
    gap: 4px;
    align-self: flex-start;
    font: inherit;
    font-size: 0.875rem;
    font-weight: 500;
    border: none;
    background: transparent;
    color: var(--color-primary);
    padding: 4px 8px 4px 0;
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
  }
  .ch-rooms-back:hover {
    text-decoration: underline;
  }
  .ch-rooms-back:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
    border-radius: var(--radius-sm);
  }
  .ch-room-head {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .ch-room-icon {
    font-size: 2rem;
    line-height: 1;
    flex-shrink: 0;
  }
  .ch-room-head-text {
    flex: 1;
    min-width: 0;
  }
  .ch-room-name {
    margin: 0;
    font-size: 1.375rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .ch-room-sub {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-room-cards {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  .ch-rooms-empty {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-rooms-empty-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
  }
</style>
