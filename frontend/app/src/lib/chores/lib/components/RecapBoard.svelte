<script lang="ts">
  import type { ChoreRecapDto } from '../types';

  // ───────────────────────────────────────────────────────────────────────
  // Recap lens — the in-app view of the weekly recap. Two parts:
  //   1. THIS WEEK — the SAME content the Discord digest posts (collective
  //      headline, per-member effort split, "needs attention" chores, "up for
  //      grabs" count). So the app and the channel never tell a different story.
  //   2. WEEK OVER WEEK — a column chart of completions per week (the trend the
  //      digest can't show), current week highlighted as the partial last bar.
  //
  // ⚠ Framing (mirrors the digest, M12/MN8): collective + non-punitive. The
  //   distribution is neutral (alphabetical, no ranking); "needs attention" names
  //   CHORES, not people. No leaderboard.
  // ⚠ MN9 — NO client date math. `weekStartLocal` is a "YYYY-MM-DD" string already
  //   resolved in the household timezone server-side; we format it by SPLITTING the
  //   parts (never `new Date('YYYY-MM-DD')`, which would shift the day in US zones).
  // ⚠ All values are server-computed; this is a pure render plus loading/empty/error.
  //   The store owns the fetch + cache invalidation.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    recap: ChoreRecapDto | null;
    loading: boolean;
    error: string | null;
    /** Retry after an error (re-runs loadRecap). */
    onRetry: () => void;
  }

  let { recap, loading, error, onRetry }: Props = $props();

  const MONTHS = [
    'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
    'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
  ];

  /**
   * Format a "YYYY-MM-DD" week-start as a short "Mon D" label. MN9-safe: we parse
   * the parts ourselves and NEVER construct a Date from a date-only string.
   */
  function weekLabel(iso: string): string {
    const parts = iso.split('-');
    const m = Number.parseInt(parts[1] ?? '', 10);
    const d = Number.parseInt(parts[2] ?? '', 10);
    if (!m || !d || m < 1 || m > 12) return iso;
    return `${MONTHS[m - 1]} ${d}`;
  }

  /** Percent without trailing-zero noise (25 → "25", 41.7 → "41.7"). Values arrive 0..100. */
  function pct(value: number): string {
    return Number.isInteger(value) ? String(value) : value.toFixed(1);
  }

  let current = $derived(recap?.current ?? null);
  let trend = $derived(recap?.trend ?? []);

  // Show the per-member split only when there's a real multi-member week.
  let hasDistribution = $derived(
    current != null && current.distribution.length > 0 && current.totalCompletions > 0,
  );

  // Column-chart scaling: tallest bar = the busiest week (min 1 to avoid /0).
  let maxCompletions = $derived(
    Math.max(1, ...trend.map((t) => t.totalCompletions)),
  );

  // Only worth drawing the trend if there's more than the current (partial) week,
  // or any past activity to compare against.
  let hasTrend = $derived(trend.length > 1);

  // ── Phase 15 (logbook) additions ────────────────────────────────────────
  //
  // ⚠ MN2/E1 (load-bearing): the per-week distribution is a WITHIN-week breakdown
  //   on ONE selected week — NEVER a single member's value drawn as a line across
  //   weeks (that rebuilds the dropped "B" scoreboard). The week selection is
  //   COMPONENT-LOCAL state (not the store); it defaults to the current week.

  /** The selected week's `weekStartLocal`, or null ⇒ fall back to the current/latest week. */
  let selectedWeek = $state<string | null>(null);

  let selectedWeekLocal = $derived(
    selectedWeek ??
      trend.find((t) => t.isCurrent)?.weekStartLocal ??
      trend.at(-1)?.weekStartLocal ??
      null,
  );
  let selectedPoint = $derived(
    trend.find((t) => t.weekStartLocal === selectedWeekLocal) ?? null,
  );

  function selectWeek(weekStartLocal: string): void {
    selectedWeek = weekStartLocal;
  }

  let milestones = $derived(recap?.milestones ?? null);
  let keptMoments = $derived(recap?.keptMoments ?? []);
  let whatGotTended = $derived(recap?.whatGotTended ?? []);
  let goneQuiet = $derived(recap?.goneQuiet ?? []);

  // What-got-tended bar scaling (min 1 to avoid /0).
  let maxTended = $derived(Math.max(1, ...whatGotTended.map((t) => t.completions)));
</script>

<div class="ch-recap">
  {#if error}
    <div class="ch-recap-error" role="alert">
      <span>{error}</span>
      <button type="button" class="ch-recap-retry" onclick={onRetry}>Retry</button>
    </div>
  {:else if loading && recap == null}
    <div class="ch-recap-state">Loading the recap…</div>
  {:else if current != null}
    <!-- ── This week ─────────────────────────────────────────────────────── -->
    <section class="ch-recap-current" aria-label="This week's recap">
      <header class="ch-recap-head">
        <p class="ch-recap-eyebrow">Week of {weekLabel(current.weekStartLocal)}</p>
        <h2 class="ch-recap-headline">{current.headline}</h2>
      </header>

      <div class="ch-recap-totals">
        <div class="ch-recap-total">
          <span class="ch-recap-total-num">{current.totalCompletions}</span>
          <span class="ch-recap-total-label">
            {current.totalCompletions === 1 ? 'chore done' : 'chores done'}
          </span>
        </div>
        <div class="ch-recap-total">
          <span class="ch-recap-total-num">{current.totalPoints}</span>
          <span class="ch-recap-total-label">
            {current.totalPoints === 1 ? 'point' : 'points'}
          </span>
        </div>
        <div class="ch-recap-total">
          <span class="ch-recap-total-num">{current.upForGrabsCount}</span>
          <span class="ch-recap-total-label">up for grabs</span>
        </div>
      </div>

      {#if hasDistribution}
        <!-- Per-member effort split — neutral (alphabetical, no ranking). Bar width
             is the member's actual share (`sharePct`, percent 0..100 — used directly). -->
        <ul class="ch-recap-rows" aria-label="This week's effort by person">
          {#each current.distribution as member (member.displayName)}
            <li class="ch-recap-row">
              <span class="ch-recap-name">{member.displayName}</span>
              <span class="ch-recap-track">
                <span
                  class="ch-recap-bar"
                  style="width: {member.sharePct}%;"
                  aria-hidden="true"
                ></span>
              </span>
              <span class="ch-recap-figures">
                <span class="ch-recap-share">{pct(member.sharePct)}%</span>
                <span class="ch-recap-points">
                  {member.points}
                  {member.points === 1 ? 'pt' : 'pts'}
                </span>
              </span>
            </li>
          {/each}
        </ul>
      {:else}
        <p class="ch-recap-empty-week">Nothing logged this week yet — it fills in as chores get done.</p>
      {/if}

      {#if current.fallingBehind.length > 0}
        <!-- Needs attention — names CHORES, not people (M12/MN8). Same list the digest sends. -->
        <div class="ch-recap-attention">
          <span class="ch-recap-attention-label">⏰ Needs attention</span>
          <ul class="ch-recap-chips">
            {#each current.fallingBehind as name (name)}
              <li class="ch-recap-chip">{name}</li>
            {/each}
          </ul>
        </div>
      {/if}
    </section>

    <!-- ── Week over week ────────────────────────────────────────────────── -->
    {#if hasTrend}
      <section class="ch-recap-trend" aria-label="Chores completed each week">
        <header class="ch-recap-trend-head">
          <h3 class="ch-recap-trend-title">Week over week</h3>
          <p class="ch-recap-trend-sub">Chores completed each week — this week is the last bar.</p>
        </header>
        <ol class="ch-recap-chart">
          {#each trend as week (week.weekStartLocal)}
            <li class="ch-recap-col" class:is-current={week.isCurrent} class:is-selected={week.weekStartLocal === selectedWeekLocal}>
              <!-- The column doubles as the week selector for the breakdown below (Phase 15). A real
                   <button> keeps click + keyboard (Enter/Space) native and accessible. -->
              <button
                type="button"
                class="ch-recap-col-btn"
                aria-pressed={week.weekStartLocal === selectedWeekLocal}
                aria-label="Show {week.isCurrent ? 'this week' : weekLabel(week.weekStartLocal)}'s breakdown"
                onclick={() => selectWeek(week.weekStartLocal)}
              >
                <span class="ch-recap-col-num">{week.totalCompletions}</span>
                <span class="ch-recap-col-track" aria-hidden="true">
                  <span
                    class="ch-recap-col-bar"
                    style="height: {(week.totalCompletions / maxCompletions) * 100}%;"
                  ></span>
                </span>
                <span class="ch-recap-col-label">
                  {week.isCurrent ? 'This week' : weekLabel(week.weekStartLocal)}
                </span>
              </button>
            </li>
          {/each}
        </ol>
      </section>
    {/if}

    <!-- ══ Phase 15 logbook sections (APPENDED — the sections above are unchanged) ══ -->

    <!-- ── Per-week breakdown (a WITHIN-week split on the selected week — never a
            per-person line across weeks, MN2/E1) ──────────────────────────── -->
    {#if hasTrend && selectedPoint != null}
      <section class="ch-recap-breakdown" aria-label="Breakdown for the selected week">
        <header class="ch-recap-trend-head">
          <h3 class="ch-recap-trend-title">Who tended what</h3>
          <p class="ch-recap-trend-sub">
            {selectedPoint.isCurrent ? 'This week' : `Week of ${weekLabel(selectedPoint.weekStartLocal)}`} —
            tap a bar above to look back.
          </p>
        </header>
        {#if selectedPoint.distribution.length > 0 && selectedPoint.totalPoints > 0}
          <ul class="ch-recap-rows" aria-label="Effort by person for the selected week">
            {#each selectedPoint.distribution as member (member.displayName)}
              <li class="ch-recap-row">
                <span class="ch-recap-name">{member.displayName}</span>
                <span class="ch-recap-track">
                  <span class="ch-recap-bar" style="width: {member.sharePct}%;" aria-hidden="true"></span>
                </span>
                <span class="ch-recap-figures">
                  <span class="ch-recap-share">{pct(member.sharePct)}%</span>
                  <span class="ch-recap-points">{member.points} {member.points === 1 ? 'pt' : 'pts'}</span>
                </span>
              </li>
            {/each}
          </ul>
        {:else}
          <p class="ch-recap-empty-week">No completions this week — a quiet stretch.</p>
        {/if}
      </section>
    {/if}

    <!-- ── Milestones (collective — effort/count facts, no ranking) ────────── -->
    {#if milestones != null && (milestones.bestWeek != null || milestones.seasonTotalCompletions > 0 || milestones.firstEvers.length > 0)}
      <section class="ch-recap-milestones" aria-label="Milestones">
        <h3 class="ch-recap-trend-title">Milestones</h3>
        <div class="ch-recap-stat-grid">
          <div class="ch-recap-stat">
            <span class="ch-recap-stat-num">{milestones.seasonTotalCompletions}</span>
            <span class="ch-recap-stat-label">chores this season</span>
          </div>
          <div class="ch-recap-stat">
            <span class="ch-recap-stat-num">{milestones.seasonTotalPoints}</span>
            <span class="ch-recap-stat-label">points this season</span>
          </div>
          <div class="ch-recap-stat">
            <span class="ch-recap-stat-num">{milestones.longestActiveStreakWeeks}</span>
            <span class="ch-recap-stat-label">
              {milestones.longestActiveStreakWeeks === 1 ? 'week streak' : 'weeks streak'}
            </span>
          </div>
          {#if milestones.bestWeek != null}
            <div class="ch-recap-stat">
              <span class="ch-recap-stat-num">{milestones.bestWeek.totalCompletions}</span>
              <span class="ch-recap-stat-label">best week ({weekLabel(milestones.bestWeek.weekStartLocal)})</span>
            </div>
          {/if}
        </div>
        {#if milestones.firstEvers.length > 0}
          <div class="ch-recap-firsts">
            <span class="ch-recap-firsts-label">🌱 First time this season</span>
            <ul class="ch-recap-chips">
              {#each milestones.firstEvers as first (first.choreName + first.localDate)}
                <li class="ch-recap-chip solid">{first.choreName}</li>
              {/each}
            </ul>
          </div>
        {/if}
      </section>
    {/if}

    <!-- ── Kept moments (notes & photos — the pride-forward highlights) ────── -->
    {#if keptMoments.length > 0}
      <section class="ch-recap-moments" aria-label="Kept moments">
        <h3 class="ch-recap-trend-title">Kept moments</h3>
        <ul class="ch-recap-moment-list">
          {#each keptMoments as moment, i (i)}
            <li class="ch-recap-moment">
              <div class="ch-recap-moment-head">
                <span class="ch-recap-moment-chore">{moment.choreName}</span>
                <span class="ch-recap-moment-date">{weekLabel(moment.localDate)}</span>
                {#if moment.hasPhoto}<span class="ch-recap-moment-photo" title="has a photo">📷</span>{/if}
              </div>
              {#if moment.note}<p class="ch-recap-moment-note">{moment.note}</p>{/if}
            </li>
          {/each}
        </ul>
      </section>
    {/if}

    <!-- ── What got tended (per-room — roomless bucketed as "General") ─────── -->
    {#if whatGotTended.length > 0}
      <section class="ch-recap-tended" aria-label="What got tended">
        <h3 class="ch-recap-trend-title">What got tended</h3>
        <ul class="ch-recap-tended-list">
          {#each whatGotTended as room (room.roomName)}
            <li class="ch-recap-tended-row">
              <span class="ch-recap-name">{room.roomName}</span>
              <span class="ch-recap-track">
                <span
                  class="ch-recap-bar"
                  style="width: {(room.completions / maxTended) * 100}%;"
                  aria-hidden="true"
                ></span>
              </span>
              <span class="ch-recap-points">{room.completions}</span>
            </li>
          {/each}
        </ul>
      </section>
    {/if}

    <!-- ── Quiet corners (the shared gone-quiet band — framed collectively) ── -->
    {#if goneQuiet.length > 0}
      <section class="ch-recap-quiet" aria-label="Quiet corners">
        <h3 class="ch-recap-trend-title">Quiet corners</h3>
        <p class="ch-recap-trend-sub">Chores that have gone quiet — a gap belongs to the chore, never a person.</p>
        <ul class="ch-recap-quiet-list">
          {#each goneQuiet as q (q.choreName)}
            <li class="ch-recap-qcard">
              <div class="ch-recap-qhead">
                <span class="ch-recap-qname">{q.choreName}</span>
                <span class="ch-recap-pill" class:soft={q.reason === 'slipped'}>{q.reason}</span>
              </div>
              <p class="ch-recap-qmeta">
                {q.cadenceLabel} ·
                {#if q.lastCompletedLocalDate}
                  last done {weekLabel(q.lastCompletedLocalDate)}
                {:else}
                  never done yet
                {/if}
              </p>
            </li>
          {/each}
        </ul>
      </section>
    {/if}
  {:else}
    <div class="ch-recap-state">
      <p class="ch-recap-empty-head">No recap yet.</p>
      <p>The weekly recap fills in as chores get completed.</p>
    </div>
  {/if}
</div>

<style>
  .ch-recap {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  /* ── This week ──────────────────────────────────────────────────────────── */
  .ch-recap-current {
    display: flex;
    flex-direction: column;
    gap: 16px;
    padding: 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-recap-head {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .ch-recap-eyebrow {
    margin: 0;
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  .ch-recap-headline {
    margin: 0;
    font-size: 1.125rem;
    font-weight: 600;
    line-height: 1.3;
    color: var(--color-text);
  }

  .ch-recap-totals {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
  }
  .ch-recap-total {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 2px;
    padding: 10px 18px;
    background: var(--color-surface);
    border-radius: var(--radius-sm);
    min-width: 84px;
  }
  .ch-recap-total-num {
    font-size: 1.5rem;
    font-weight: 700;
    color: var(--color-text);
    font-variant-numeric: tabular-nums;
    line-height: 1;
  }
  .ch-recap-total-label {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }

  /* Per-member effort rows (neutral single fill — no ranking color). */
  .ch-recap-rows {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .ch-recap-row {
    display: grid;
    grid-template-columns: minmax(96px, 1fr) minmax(0, 2.4fr) minmax(72px, auto);
    align-items: center;
    gap: 12px;
  }
  .ch-recap-name {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }
  .ch-recap-track {
    position: relative;
    height: 16px;
    background: var(--color-surface);
    border-radius: 8px;
    overflow: hidden;
  }
  .ch-recap-bar {
    position: absolute;
    inset: 0 auto 0 0;
    height: 100%;
    background: var(--color-primary);
    border-radius: 8px;
    transition: width 0.2s ease;
  }
  .ch-recap-figures {
    display: flex;
    flex-direction: column;
    align-items: flex-end;
    line-height: 1.2;
    text-align: right;
  }
  .ch-recap-share {
    font-size: 0.875rem;
    font-weight: 600;
    color: var(--color-text);
    font-variant-numeric: tabular-nums;
  }
  .ch-recap-points {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .ch-recap-empty-week {
    margin: 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }

  /* Needs attention — chore-name chips. */
  .ch-recap-attention {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .ch-recap-attention-label {
    font-size: 0.8125rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-recap-chips {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-wrap: wrap;
    gap: 6px;
  }
  .ch-recap-chip {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
    background: var(--color-surface);
    border: 1px dashed var(--color-line-strong);
    border-radius: var(--radius-sm);
    padding: 5px 10px;
  }

  /* ── Week over week (column chart) ──────────────────────────────────────── */
  .ch-recap-trend {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-recap-trend-head {
    display: flex;
    flex-direction: column;
    gap: 2px;
  }
  .ch-recap-trend-title {
    margin: 0;
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-recap-trend-sub {
    margin: 0;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .ch-recap-chart {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    align-items: flex-end;
    gap: 6px;
    min-height: 140px;
  }
  .ch-recap-col {
    flex: 1 1 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    min-width: 0;
  }
  .ch-recap-col-num {
    font-size: 0.8125rem;
    font-weight: 600;
    color: var(--color-text-muted);
    font-variant-numeric: tabular-nums;
  }
  .ch-recap-col-track {
    display: flex;
    align-items: flex-end;
    justify-content: center;
    width: 100%;
    height: 96px;
  }
  .ch-recap-col-bar {
    width: 70%;
    max-width: 36px;
    min-height: 3px;
    background: var(--color-line-strong);
    border-radius: 4px 4px 0 0;
    transition: height 0.2s ease;
  }
  /* The current (in-progress) week reads as the live bar. */
  .ch-recap-col.is-current .ch-recap-col-bar {
    background: var(--color-primary);
  }
  .ch-recap-col.is-current .ch-recap-col-num,
  .ch-recap-col.is-current .ch-recap-col-label {
    color: var(--color-text);
    font-weight: 600;
  }
  .ch-recap-col-label {
    font-size: 0.6875rem;
    color: var(--color-text-muted);
    text-align: center;
    white-space: nowrap;
    overflow: hidden;
    text-overflow: ellipsis;
    max-width: 100%;
  }

  /* The column is a selector button (Phase 15) — reset chrome, keep the flex-column layout. */
  .ch-recap-col-btn {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 6px;
    width: 100%;
    border: none;
    background: none;
    padding: 4px 0;
    margin: 0;
    font: inherit;
    color: inherit;
    cursor: pointer;
    border-radius: var(--radius-sm);
  }
  .ch-recap-col-btn:hover {
    background: var(--color-surface);
  }
  .ch-recap-col.is-selected .ch-recap-col-btn {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
  }
  .ch-recap-col.is-selected .ch-recap-col-num,
  .ch-recap-col.is-selected .ch-recap-col-label {
    color: var(--color-text);
    font-weight: 600;
  }

  /* ── Phase 15 logbook sections (all share the trend section's card styling) ── */
  .ch-recap-breakdown,
  .ch-recap-milestones,
  .ch-recap-moments,
  .ch-recap-tended,
  .ch-recap-quiet {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-recap-quiet {
    border: 1px dashed var(--color-line-strong);
  }

  /* Milestones — a compact stat grid + first-time chips. */
  .ch-recap-stat-grid {
    display: flex;
    flex-wrap: wrap;
    gap: 10px;
  }
  .ch-recap-stat {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 2px;
    padding: 10px 16px;
    background: var(--color-surface);
    border-radius: var(--radius-sm);
    min-width: 96px;
    flex: 1 1 96px;
    text-align: center;
  }
  .ch-recap-stat-num {
    font-size: 1.375rem;
    font-weight: 700;
    color: var(--color-text);
    font-variant-numeric: tabular-nums;
    line-height: 1;
  }
  .ch-recap-stat-label {
    font-size: 0.6875rem;
    color: var(--color-text-muted);
    line-height: 1.3;
  }
  .ch-recap-firsts {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .ch-recap-firsts-label {
    font-size: 0.8125rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-recap-chip.solid {
    background: var(--color-action-hover);
    border: 1px solid var(--color-line-strong);
    border-style: solid;
    color: var(--color-text);
  }

  /* Kept moments — note/photo highlights. */
  .ch-recap-moment-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .ch-recap-moment {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding-left: 10px;
    border-left: 2px solid var(--color-line);
  }
  .ch-recap-moment-head {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: 8px;
  }
  .ch-recap-moment-chore {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .ch-recap-moment-date {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .ch-recap-moment-photo {
    font-size: 0.75rem;
  }
  .ch-recap-moment-note {
    margin: 0;
    font-size: 0.8125rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.5;
  }

  /* What got tended — per-room tally rows (reuse .ch-recap-name/-track/-bar/-points). */
  .ch-recap-tended-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 10px;
  }
  .ch-recap-tended-row {
    display: grid;
    grid-template-columns: minmax(96px, 1fr) minmax(0, 2.4fr) minmax(36px, auto);
    align-items: center;
    gap: 12px;
  }

  /* Quiet corners — shared gone-quiet cards. */
  .ch-recap-quiet-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .ch-recap-qcard {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding-bottom: 12px;
    border-bottom: 1px dotted var(--color-line);
  }
  .ch-recap-qcard:last-child {
    border-bottom: none;
    padding-bottom: 0;
  }
  .ch-recap-qhead {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ch-recap-qname {
    font-size: 0.9375rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .ch-recap-pill {
    font-size: 0.625rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    border: 1px solid var(--color-primary);
    color: var(--color-primary);
    border-radius: 999px;
    padding: 2px 8px;
  }
  .ch-recap-pill.soft {
    border-color: var(--color-line-strong);
    color: var(--color-text-muted);
  }
  .ch-recap-qmeta {
    margin: 0;
    font-size: 0.75rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.5;
  }

  /* ── Loading / empty / error ────────────────────────────────────────────── */
  .ch-recap-state {
    padding: 40px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-recap-empty-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
  }
  .ch-recap-error {
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
  .ch-recap-retry {
    font: inherit;
    border: none;
    cursor: pointer;
    border-radius: var(--radius-sm);
    padding: 4px 12px;
    background: transparent;
    color: var(--color-error);
    font-weight: 500;
  }
  .ch-recap-retry:hover {
    background: rgba(229, 57, 53, 0.12);
  }

  @media (max-width: 560px) {
    .ch-recap-row {
      grid-template-columns: 1fr;
      gap: 6px;
    }
    .ch-recap-figures {
      flex-direction: row;
      align-items: baseline;
      justify-content: space-between;
      gap: 8px;
      text-align: left;
    }
  }
</style>
