<script lang="ts">
  import type { ChoreEquityDto, EquityWindow, CapacityTier } from '../types';
  import MemberAvatar from '$lib/shared/MemberAvatar.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // Equity lens (the headline v1.1 view) — a NEUTRAL distribution of who has
  // carried the household's completion load over a window. NOT a leaderboard:
  // no ranking, no medals, no "winner/loser", no "behind" labels (M12/MN8).
  // It's a glanceable "how is the load spread", with a neutral equal-share
  // reference line so over/under the even split reads as information, not blame.
  //
  // ⚠ Contract: `sharePct` / `equalSharePct` are PERCENT 0..100 (WP-02). The bar
  //   width and the printed value use them DIRECTLY — NO client `* 100`.
  // ⚠ The falling-behind / up-for-grabs counts come STRAIGHT off the equity
  //   payload (WP-02 added them) — we never cross-reference the board DTO.
  // ⚠ All values are server-computed; this component does NO date math (MN9),
  //   no `new Date('YYYY-MM-DD')`, no dueness/decay recompute.
  //
  // The store owns the fetch + window switch + cache invalidation; this is a
  // pure render of `equity` plus loading/empty/error states. The week/all toggle
  // calls `onWindow`, which updates the store's `equityWindow` and triggers a
  // reload via App.svelte's $effect.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    equity: ChoreEquityDto | null;
    window: EquityWindow;
    loading: boolean;
    error: string | null;
    /** Switch the reporting window (week/all). Reload happens via the App effect. */
    onWindow: (window: EquityWindow) => void;
    /** Retry after an error (re-runs loadEquity for the current window). */
    onRetry: () => void;
    /** Set the CURRENT user's own physical-capacity tier (self-only — MN5). */
    onCapacity: (tier: CapacityTier) => void;
    /** True while a capacity PATCH is in flight (disables the selector). */
    savingCapacity: boolean;
  }

  let { equity, window, loading, error, onWindow, onRetry, onCapacity, savingCapacity }: Props =
    $props();

  const WINDOWS: { id: EquityWindow; label: string }[] = [
    { id: 'week', label: 'This week' },
    { id: 'all', label: 'All time' },
  ];

  // ── Self-only capacity selector (Phase 15 WP-06, MN5/M6) ──────────────────
  // The current user sets ONLY their own physical-capacity tier. Descriptive,
  // never evaluative (M6): "Full" / "Reduced" / "Minimal" describe how much
  // physical work a member can take on right now — NOT a performance rating.
  // The selected tier reshapes each member's EXPECTED reference line (below);
  // it is NOT a target/quota or a blame label.
  const CAPACITY_TIERS: { id: CapacityTier; label: string }[] = [
    { id: 'Full', label: 'Full' },
    { id: 'Reduced', label: 'Reduced' },
    { id: 'Minimal', label: 'Minimal' },
  ];

  // The caller's current tier rides the equity payload (null ⇒ Full). No separate fetch (M7/P4).
  let callerTier = $derived<CapacityTier>(equity?.callerCapacityTier ?? 'Full');

  // Render a percent without trailing-zero noise (e.g. 25 → "25", 41.7 → "41.7").
  // Pure number formatting — NOT date math. Values already arrive as 0..100.
  function pct(value: number): string {
    return Number.isInteger(value) ? String(value) : value.toFixed(1);
  }

  // Show the strip only when there is a real multi-member distribution; a solo
  // household (or empty window) is noise as a distribution.
  let hasDistribution = $derived(
    equity != null && equity.members.length > 0 && equity.totalCompletions > 0,
  );

  // ── Planning footprint (Phase 15) ────────────────────────────────────────
  // The four planning lanes (chores set up / recipes / list items / hand-offs)
  // are ALL-TIME, un-blended labeled tallies riding `equity.planning` (no new
  // fetch — M7). They show INDEPENDENTLY of the physical distribution gate above:
  // a low-physical, high-planning member (the founding case) must still surface,
  // so this section renders whenever there is ANY planning signal even if
  // `totalCompletions` is 0. NEVER sum the lanes, NEVER blend with points (MN4);
  // neutral framing only — no winner/loser, no ranking (M6). Order by display
  // name (stable, neutral — mirrors the physical rows).
  let planning = $derived(equity?.planning ?? []);

  let hasPlanningSignal = $derived(
    planning.some(
      (m) =>
        m.choresSetUp + m.recipesAdded + m.listItemsCurated + m.handOffs > 0,
    ),
  );

  let planningRows = $derived(
    [...planning].sort((a, b) => a.displayName.localeCompare(b.displayName)),
  );
</script>

<div class="ch-equity">
  <header class="ch-equity-head">
    <div class="ch-equity-titles">
      <h2 class="ch-equity-title">Physical board-load — {window === 'all' ? 'all time' : 'this week'}</h2>
      <p class="ch-equity-sub">How the chore load has been shared — not a scoreboard.</p>
    </div>
    <div class="ch-equity-windows" role="group" aria-label="Equity window">
      {#each WINDOWS as w (w.id)}
        <button
          type="button"
          class="ch-equity-window"
          class:active={window === w.id}
          aria-pressed={window === w.id}
          onclick={() => onWindow(w.id)}
        >
          {w.label}
        </button>
      {/each}
    </div>
  </header>

  <!-- Self-only physical-capacity selector (Phase 15 WP-06). The current user sets ONLY their own tier
       (MN5). Descriptive, not evaluative (M6): it reshapes that member's EXPECTED reference line below —
       it is never a score, target, or blame label. -->
  <div class="ch-cap">
    <span class="ch-cap-label" id="ch-cap-label">My physical capacity</span>
    <div class="ch-cap-options" role="group" aria-labelledby="ch-cap-label">
      {#each CAPACITY_TIERS as tier (tier.id)}
        <button
          type="button"
          class="ch-cap-option"
          class:active={callerTier === tier.id}
          aria-pressed={callerTier === tier.id}
          disabled={savingCapacity}
          onclick={() => onCapacity(tier.id)}
        >
          {tier.label}
        </button>
      {/each}
    </div>
    <p class="ch-cap-hint">
      Sets your own expected share of physical chores — descriptive, not a target.
    </p>
  </div>

  {#if error}
    <div class="ch-equity-error" role="alert">
      <span>{error}</span>
      <button type="button" class="ch-equity-retry" onclick={onRetry}>Retry</button>
    </div>
  {:else if loading && equity == null}
    <div class="ch-equity-state">Loading the household load…</div>
  {:else if equity != null && hasDistribution}
    <section class="ch-equity-dist" aria-label="Per-member share of the load">
      <!-- Each row is a proportional bar at the member's actual share (`sharePct`).
           A neutral reference marker sits at THAT member's capacity-weighted EXPECTED
           share (`expectedSharePct`, Phase 15 WP-06) — so a Reduced/Minimal member's
           reference shifts to reflect their stated capacity, not a single flat line.
           Descriptive only — no ranking, no highlight of high/low (M6). Server values
           verbatim — NO client math (M7/MN9). -->
      <ul class="ch-equity-rows">
        {#each equity.members as member (member.userId)}
          <li class="ch-equity-row">
            <span class="ch-equity-who">
              <MemberAvatar
                name={member.displayName}
                initials={member.initials}
                pictureUrl={member.pictureUrl}
                size={28}
              />
              <span class="ch-equity-name">{member.displayName}</span>
            </span>
            <span class="ch-equity-track">
              <span
                class="ch-equity-bar"
                style="width: {member.sharePct}%;"
                aria-hidden="true"
              ></span>
              <span
                class="ch-equity-ref"
                style="left: {member.expectedSharePct}%;"
                aria-hidden="true"
              ></span>
            </span>
            <span class="ch-equity-figures">
              <span class="ch-equity-share">{pct(member.sharePct)}%</span>
              <span class="ch-equity-points">
                {member.points}
                {member.points === 1 ? 'pt' : 'pts'} · {member.completions} done
              </span>
            </span>
          </li>
        {/each}
      </ul>

      <p class="ch-equity-legend">
        <span class="ch-equity-legend-ref" aria-hidden="true"></span>
        The marker shows each person's expected share, adjusted for their stated capacity.
      </p>

      <!-- Outstanding work — discrete entries, distinct from the completed-effort
           bars above. "Up for grabs" = active chores nobody owns (AssignmentKind.None
           or a stale claim); "Falling behind" = active chores past due. These are
           household-level CHORE counts (never a per-person blame label, M12/MN8), and
           come straight off the equity payload (no board cross-reference). -->
      <div class="ch-equity-outstanding" aria-label="Outstanding work">
        <div class="ch-equity-stat" aria-label="{equity.upForGrabsCount} up for grabs">
          <span class="ch-equity-stat-icon" aria-hidden="true">🙋</span>
          <span class="ch-equity-stat-count">{equity.upForGrabsCount}</span>
          <span class="ch-equity-stat-label">up for grabs</span>
        </div>
        <div class="ch-equity-stat" aria-label="{equity.fallingBehindCount} falling behind">
          <span class="ch-equity-stat-icon" aria-hidden="true">⏳</span>
          <span class="ch-equity-stat-count">{equity.fallingBehindCount}</span>
          <span class="ch-equity-stat-label">falling behind</span>
        </div>
      </div>
    </section>
  {:else}
    <div class="ch-equity-state">
      <p class="ch-equity-empty-head">Nothing logged yet.</p>
      <p>No completions in this window — the load picture fills in as chores get done.</p>
    </div>
  {/if}

  <!-- ── Planning footprint (Phase 15) ─────────────────────────────────────
       All-time system-building & coordination labor, as neutral labeled
       tallies per member. Gated on its OWN `hasPlanningSignal` — independent of
       the physical distribution above — so a low-physical, high-planning member
       still shows even when this window has zero completions. NO summed total,
       NO blended score (MN4); descriptive only, ordered by name (M6). Rides
       `equity.planning` — no new fetch (M7). -->
  {#if equity != null && hasPlanningSignal}
    <section class="ch-plan" aria-label="System-building and coordination, all time">
      <header class="ch-plan-head">
        <h3 class="ch-plan-title">System-building &amp; coordination — all time</h3>
        <p class="ch-plan-sub">
          Setup and coordination work, counted across all time — separate from the
          physical load above, never blended into a single score.
        </p>
      </header>
      <ul class="ch-plan-rows">
        {#each planningRows as member (member.userId)}
          <li class="ch-plan-row">
            <span class="ch-plan-name">{member.displayName}</span>
            <span class="ch-plan-tallies">
              <span class="ch-plan-tally">
                {member.choresSetUp}
                {member.choresSetUp === 1 ? 'chore set up' : 'chores set up'}
              </span>
              <span class="ch-plan-dot" aria-hidden="true">·</span>
              <span class="ch-plan-tally">
                {member.recipesAdded}
                {member.recipesAdded === 1 ? 'recipe' : 'recipes'}
              </span>
              <span class="ch-plan-dot" aria-hidden="true">·</span>
              <span class="ch-plan-tally">
                {member.listItemsCurated}
                {member.listItemsCurated === 1 ? 'list item' : 'list items'}
              </span>
              <span class="ch-plan-dot" aria-hidden="true">·</span>
              <span class="ch-plan-tally">
                {member.handOffs}
                {member.handOffs === 1 ? 'hand-off' : 'hand-offs'}
              </span>
            </span>
          </li>
        {/each}
      </ul>
    </section>
  {/if}
</div>

<style>
  .ch-equity {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .ch-equity-head {
    display: flex;
    align-items: flex-start;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
  }
  .ch-equity-titles {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .ch-equity-title {
    margin: 0;
    font-size: 1.125rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-equity-sub {
    margin: 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }

  /* ── Window toggle ────────────────────────────────────────────────────── */
  .ch-equity-windows {
    display: inline-flex;
    gap: 2px;
    padding: 3px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-equity-window {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    padding: 6px 14px;
    min-height: 34px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    white-space: nowrap;
    transition:
      background-color 0.15s,
      color 0.15s;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-equity-window:hover:not(.active) {
    color: var(--color-text);
  }
  .ch-equity-window.active {
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-1);
  }
  .ch-equity-window:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: -2px;
  }

  /* ── Self-only capacity selector ──────────────────────────────────────── */
  .ch-cap {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .ch-cap-label {
    font-size: 0.8125rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-cap-options {
    display: inline-flex;
    gap: 2px;
    padding: 3px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
    align-self: flex-start;
  }
  .ch-cap-option {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    padding: 6px 14px;
    min-height: 34px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    white-space: nowrap;
    transition:
      background-color 0.15s,
      color 0.15s;
    -webkit-tap-highlight-color: transparent;
    touch-action: manipulation;
  }
  .ch-cap-option:hover:not(.active):not(:disabled) {
    color: var(--color-text);
  }
  .ch-cap-option.active {
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-1);
  }
  .ch-cap-option:disabled {
    cursor: default;
    opacity: 0.7;
  }
  .ch-cap-option:focus-visible {
    outline: 2px solid var(--color-primary);
    outline-offset: -2px;
  }
  .ch-cap-hint {
    margin: 0;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }

  /* ── Distribution rows ────────────────────────────────────────────────── */
  .ch-equity-dist {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-equity-rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 14px;
  }
  .ch-equity-row {
    display: grid;
    grid-template-columns: minmax(120px, 1fr) minmax(0, 2.4fr) minmax(96px, auto);
    align-items: center;
    gap: 12px;
  }
  .ch-equity-who {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .ch-equity-name {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  /* The bar track + neutral equal-share reference line. */
  .ch-equity-track {
    position: relative;
    height: 16px;
    background: var(--color-surface);
    border-radius: 8px;
    overflow: hidden;
  }
  .ch-equity-bar {
    position: absolute;
    inset: 0 auto 0 0;
    height: 100%;
    /* Neutral single fill — no per-member ranking color, no good/bad scale. */
    background: var(--color-primary);
    border-radius: 8px;
    transition: width 0.2s ease;
  }
  .ch-equity-ref {
    position: absolute;
    top: -3px;
    bottom: -3px;
    width: 0;
    border-left: 2px dashed var(--color-text-muted);
    transform: translateX(-1px);
  }

  .ch-equity-figures {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    line-height: 1.2;
    text-align: right;
  }
  .ch-equity-share {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--color-text);
    font-variant-numeric: tabular-nums;
  }
  .ch-equity-points {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }

  .ch-equity-legend {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .ch-equity-legend-ref {
    display: inline-block;
    width: 0;
    height: 12px;
    border-left: 2px dashed var(--color-text-muted);
  }

  /* ── Outstanding work: up-for-grabs / falling-behind ───────────────────── */
  /* Discrete entries with a dashed edge (= unclaimed / outstanding) so they read
     distinctly from the solid completed-effort member bars above. */
  .ch-equity-outstanding {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
  }
  .ch-equity-stat {
    display: inline-flex;
    align-items: center;
    gap: 7px;
    padding: 7px 12px;
    background: var(--color-surface);
    border: 1px dashed var(--color-line-strong);
    border-radius: var(--radius-sm);
  }
  .ch-equity-stat-icon {
    font-size: 0.9375rem;
    line-height: 1;
  }
  .ch-equity-stat-count {
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--color-text);
    font-variant-numeric: tabular-nums;
  }
  .ch-equity-stat-label {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }

  /* ── Planning footprint (Phase 15) ────────────────────────────────────── */
  /* Neutral labeled tallies — plain source nouns, no bars, no ranking, no
     summed total. Visually a quiet block beneath the physical distribution. */
  .ch-plan {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-plan-head {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .ch-plan-title {
    margin: 0;
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-plan-sub {
    margin: 0;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .ch-plan-rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .ch-plan-row {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .ch-plan-name {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .ch-plan-tallies {
    display: flex;
    flex-wrap: wrap;
    align-items: baseline;
    gap: 6px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-plan-tally {
    font-variant-numeric: tabular-nums;
  }
  .ch-plan-dot {
    color: var(--color-text-muted);
    opacity: 0.6;
  }

  /* ── Loading / empty / error states ───────────────────────────────────── */
  .ch-equity-state {
    padding: 40px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-equity-empty-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
  }
  .ch-equity-error {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    border-radius: var(--radius-sm);
    color: var(--color-error);
    font-size: 0.875rem;
  }
  .ch-equity-retry {
    font: inherit;
    border: none;
    cursor: pointer;
    border-radius: var(--radius-sm);
    padding: 4px 12px;
    background: transparent;
    color: var(--color-error);
    font-weight: 500;
  }
  .ch-equity-retry:hover {
    background: rgba(229, 57, 53, 0.12);
  }

  /* Stack the row on narrow viewports so the bar keeps usable width. */
  @media (max-width: 560px) {
    .ch-equity-row {
      grid-template-columns: 1fr;
      gap: 6px;
    }
    .ch-equity-figures {
      flex-direction: row;
      align-items: baseline;
      justify-content: space-between;
      gap: 8px;
      text-align: left;
    }
  }
</style>
