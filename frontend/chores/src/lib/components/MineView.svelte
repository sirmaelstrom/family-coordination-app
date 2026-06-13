<script lang="ts">
  import type { MemberLoad } from '../state.svelte';
  import type { ChoreDto } from '../types';
  import ChoreCard from './ChoreCard.svelte';
  import MemberAvatar from './MemberAvatar.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // Mine lens (S6) — the chores the current user owns or is actively holding,
  // plus a lightweight household LOAD STRIP (the v1.1 equity seed): per-member
  // count of currently-held chores. The strip is DISPLAY-ONLY over the already-
  // loaded board (no points engine, no schema change) — it's a glanceable "who's
  // carrying what right now", not a leaderboard.
  //
  // ⚠ M11: both the list and the load strip are CLIENT-SIDE groupings of the
  // ONE board payload (store.mineChores / store.memberLoads); no fetch on switch.
  // ⚠ M5/M6: no dueness/decay recompute; no client "completed today" tally
  // (would need an actor + tz day math the DTO doesn't support — see the store).
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    chores: ChoreDto[];
    loads: MemberLoad[];
    currentUserId: number;
    isPending: (choreId: number) => boolean;
    onClaim: (chore: ChoreDto) => void;
    onDrop: (chore: ChoreDto) => void;
    onComplete: (chore: ChoreDto) => void;
    onHandOff: (chore: ChoreDto) => void;
    onTake: (chore: ChoreDto) => void;
    onCommit: (chore: ChoreDto) => void;
    onLeave: (chore: ChoreDto) => void;
    onEdit: (chore: ChoreDto) => void;
  }

  let {
    chores,
    loads,
    currentUserId,
    isPending,
    onClaim,
    onDrop,
    onComplete,
    onHandOff,
    onTake,
    onCommit,
    onLeave,
    onEdit,
  }: Props = $props();

  // Deterministic per-user avatar accent (hash userId → palette). Purely
  // cosmetic, no schema change — gives each member a stable color in the strip.
  const AVATAR_PALETTE = [
    'rgb(46, 125, 50)',
    'rgb(30, 136, 229)',
    'rgb(255, 111, 0)',
    'rgb(142, 36, 170)',
    'rgb(0, 137, 123)',
    'rgb(194, 24, 91)',
  ];
  function avatarColor(userId: number): string {
    const idx = ((userId % AVATAR_PALETTE.length) + AVATAR_PALETTE.length) % AVATAR_PALETTE.length;
    return AVATAR_PALETTE[idx];
  }

  // Only show the strip when there's more than one member (it's a household
  // comparison; for a solo household it's noise).
  let showStrip = $derived(loads.length > 1);
</script>

<div class="ch-mine">
  {#if showStrip}
    <section class="ch-load-strip" aria-label="Who's carrying what">
      <h2 class="ch-load-title">Household load</h2>
      <div class="ch-load-members">
        {#each loads as load (load.member.userId)}
          <div
            class="ch-load-member"
            class:is-me={load.member.userId === currentUserId}
            style="--avatar-accent: {avatarColor(load.member.userId)};"
          >
            <span class="ch-load-avatar">
              <MemberAvatar
                name={load.member.displayName}
                initials={load.member.initials}
                pictureUrl={load.member.pictureUrl}
                size={32}
              />
            </span>
            <span class="ch-load-text">
              <span class="ch-load-name">
                {load.member.userId === currentUserId ? 'You' : load.member.displayName}
              </span>
              <span class="ch-load-count">
                {load.heldCount}
                {load.heldCount === 1 ? 'chore' : 'chores'}
              </span>
            </span>
          </div>
        {/each}
      </div>
    </section>
  {/if}

  {#if chores.length > 0}
    <section class="ch-mine-list">
      <header class="ch-mine-head">
        <h2 class="ch-mine-list-title">On your plate</h2>
        <span class="ch-mine-count">{chores.length}</span>
      </header>
      <div class="ch-mine-cards">
        {#each chores as chore (chore.id)}
          <ChoreCard
            {chore}
            {currentUserId}
            pending={isPending(chore.id)}
            {onClaim}
            {onDrop}
            {onComplete}
            {onHandOff}
            {onTake}
            {onCommit}
            {onLeave}
            {onEdit}
          />
        {/each}
      </div>
    </section>
  {:else}
    <div class="ch-mine-empty">
      <p class="ch-mine-empty-head">Nothing on your plate.</p>
      <p>You're not holding any chores right now. Check <strong>Up for grabs</strong> to pitch in.</p>
    </div>
  {/if}
</div>

<style>
  .ch-mine {
    display: flex;
    flex-direction: column;
    gap: 20px;
  }

  /* ── Household load strip (equity seed — display only) ──────────────────── */
  .ch-load-strip {
    display: flex;
    flex-direction: column;
    gap: 10px;
    padding: 14px 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-load-title {
    margin: 0;
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  .ch-load-members {
    display: flex;
    flex-wrap: wrap;
    gap: 12px;
  }
  .ch-load-member {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 6px 12px 6px 6px;
    background: var(--color-surface);
    border-radius: 22px;
    box-shadow: var(--shadow-1);
  }
  .ch-load-member.is-me {
    outline: 2px solid var(--avatar-accent);
  }
  /* Tint the avatar with the member's deterministic accent color. */
  .ch-load-avatar :global(.ch-avatar) {
    background: var(--avatar-accent);
  }
  .ch-load-text {
    display: flex;
    flex-direction: column;
    line-height: 1.2;
  }
  .ch-load-name {
    font-size: 0.8125rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-load-count {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }

  /* ── My chores list ─────────────────────────────────────────────────────── */
  .ch-mine-list {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .ch-mine-head {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ch-mine-list-title {
    margin: 0;
    font-size: 0.9375rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  .ch-mine-count {
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
  .ch-mine-cards {
    display: flex;
    flex-direction: column;
    gap: 10px;
  }

  .ch-mine-empty {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-mine-empty-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
  }
</style>
