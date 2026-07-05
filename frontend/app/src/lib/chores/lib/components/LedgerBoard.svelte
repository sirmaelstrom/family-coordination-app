<script lang="ts">
  import type { ChoreLedgerDto, LedgerEventDto, GhostDto } from '../types';

  // ───────────────────────────────────────────────────────────────────────
  // Ledger lens (C) — the browsable completion ledger. Three parts:
  //   1. THE WEAVE — a mosaic of weeks × weekdays; one stitch per finished
  //      chore, day by day. Thin days are thin, empty days are empty, and a
  //      missed expected beat renders as a hollow mark. Gaps show as gaps.
  //   2. GONE QUIET — chores that stopped appearing (≥2 trailing misses),
  //      framed as a question for the house, never a verdict on a person.
  //   3. THE RECORD — the completion feed, newest week first, grouped by day,
  //      with ghost rows (expected-but-missing beats) sitting alongside.
  //
  // ⚠ Framing (load-bearing — honest/pride-forward, NOT nagging): a gap belongs
  //   to the CHORE, never to a person. Ghosts read as gentle "this beat was
  //   missed (snoozed/slipped)". displayName only — no ranking, no scoreboard.
  // ⚠ MN9 — NO client date math on a date-only string. `localDate`/`weekStartLocal`
  //   are "YYYY-MM-DD" already resolved in the household tz server-side. We format
  //   by SPLITTING parts, and step days only via `Date.UTC(...)` on explicit numeric
  //   components (never `new Date('YYYY-MM-DD')`, which shifts the day in US zones).
  // ⚠ Pure render + loading/empty/error. The store owns fetch + cache invalidation.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    ledger: ChoreLedgerDto | null;
    loading: boolean;
    error: string | null;
    /** Retry after an error (re-runs loadLedger). */
    onRetry: () => void;
  }

  let { ledger, loading, error, onRetry }: Props = $props();

  const MONTHS = [
    'Jan', 'Feb', 'Mar', 'Apr', 'May', 'Jun',
    'Jul', 'Aug', 'Sep', 'Oct', 'Nov', 'Dec',
  ];
  const DOW = ['M', 'T', 'W', 'T', 'F', 'S', 'S'];
  const MAX_CELL_DOTS = 5;

  /** Format "YYYY-MM-DD" as "Mon D" (MN9-safe — split parts, never Date-parse a string). */
  function dayLabel(iso: string): string {
    const parts = iso.split('-');
    const m = Number.parseInt(parts[1] ?? '', 10);
    const d = Number.parseInt(parts[2] ?? '', 10);
    if (!m || !d || m < 1 || m > 12) return iso;
    return `${MONTHS[m - 1]} ${d}`;
  }

  /**
   * Step a "YYYY-MM-DD" date by `days`, MN9-safe. Uses `Date.UTC` on the EXPLICIT
   * numeric parts (not a string parse) so month/year rollover is correct and the
   * result never shifts across a timezone — the sanctioned escape from the date footgun.
   */
  function addDaysIso(iso: string, days: number): string {
    const parts = iso.split('-');
    const y = Number.parseInt(parts[0] ?? '', 10);
    const m = Number.parseInt(parts[1] ?? '', 10);
    const d = Number.parseInt(parts[2] ?? '', 10);
    const dt = new Date(Date.UTC(y, m - 1, d + days));
    const yy = dt.getUTCFullYear();
    const mm = String(dt.getUTCMonth() + 1).padStart(2, '0');
    const dd = String(dt.getUTCDate()).padStart(2, '0');
    return `${yy}-${mm}-${dd}`;
  }

  let events = $derived(ledger?.events ?? []);
  let weeks = $derived(ledger?.weeks ?? []);
  let ghosts = $derived(ledger?.ghosts ?? []);
  let goneQuiet = $derived(ledger?.goneQuiet ?? []);
  /** Server-stamped local "today" — the weave stops here; later cells are the unwritten future. */
  let today = $derived(ledger?.windowEndLocal ?? '');

  // Per-day indexes (grouped by the date STRING — never parsed). ISO dates sort lexicographically.
  let eventsByDate = $derived.by(() => {
    const map = new Map<string, LedgerEventDto[]>();
    for (const e of events) {
      const list = map.get(e.localDate);
      if (list) list.push(e);
      else map.set(e.localDate, [e]);
    }
    return map;
  });
  let ghostsByDate = $derived.by(() => {
    const map = new Map<string, GhostDto[]>();
    for (const g of ghosts) {
      const list = map.get(g.expectedLocalDate);
      if (list) list.push(g);
      else map.set(g.expectedLocalDate, [g]);
    }
    return map;
  });

  interface DayCell {
    date: string;
    count: number;
    hasGhost: boolean;
    isFuture: boolean;
    isToday: boolean;
  }
  interface WeaveWeek {
    weekStartLocal: string;
    completions: number;
    cells: DayCell[];
  }

  // The weave: one column per week (oldest→newest), each a Mon→Sun stack of day cells.
  let weaveWeeks = $derived.by((): WeaveWeek[] =>
    weeks.map((w) => ({
      weekStartLocal: w.weekStartLocal,
      completions: w.completions,
      cells: Array.from({ length: 7 }, (_, i): DayCell => {
        const date = addDaysIso(w.weekStartLocal, i);
        return {
          date,
          count: eventsByDate.get(date)?.length ?? 0,
          hasGhost: ghostsByDate.has(date),
          isFuture: today !== '' && date > today,
          isToday: date === today,
        };
      }),
    })),
  );

  interface FeedDay {
    date: string;
    events: LedgerEventDto[];
    ghosts: GhostDto[];
  }
  interface FeedWeek {
    weekStartLocal: string;
    completions: number;
    days: FeedDay[];
  }

  // The record feed: newest week first; within each, only days that carry an event or a ghost, newest first.
  let feedWeeks = $derived.by((): FeedWeek[] => {
    const out: FeedWeek[] = [];
    for (let i = weeks.length - 1; i >= 0; i--) {
      const w = weeks[i];
      const days: FeedDay[] = [];
      for (let r = 6; r >= 0; r--) {
        const date = addDaysIso(w.weekStartLocal, r);
        if (today !== '' && date > today) continue; // the unwritten future
        const dayEvents = eventsByDate.get(date) ?? [];
        const dayGhosts = ghostsByDate.get(date) ?? [];
        if (dayEvents.length === 0 && dayGhosts.length === 0) continue;
        days.push({ date, events: dayEvents, ghosts: dayGhosts });
      }
      if (days.length > 0) out.push({ weekStartLocal: w.weekStartLocal, completions: w.completions, days });
    }
    return out;
  });

  let totalEntries = $derived(events.length);
  let hasAnyRecord = $derived(events.length > 0 || ghosts.length > 0 || goneQuiet.length > 0);
</script>

<div class="ch-ledger">
  {#if error}
    <div class="ch-ledger-error" role="alert">
      <span>{error}</span>
      <button type="button" class="ch-ledger-retry" onclick={onRetry}>Retry</button>
    </div>
  {:else if loading && ledger == null}
    <div class="ch-ledger-state">Loading the ledger…</div>
  {:else if ledger != null && hasAnyRecord}
    <!-- ── Masthead ─────────────────────────────────────────────────────── -->
    <header class="ch-ledger-masthead">
      <span class="ch-ledger-hero-num">{totalEntries}</span>
      <span class="ch-ledger-hero-lab">
        {totalEntries === 1 ? 'chore written down' : 'chores written down'}
      </span>
      <p class="ch-ledger-sub">
        Every chore in this house, the moment it happened — from {dayLabel(ledger.windowStartLocal)} through today.
      </p>
    </header>

    <!-- ── The weave ────────────────────────────────────────────────────── -->
    {#if weaveWeeks.length > 0}
      <section class="ch-ledger-weave-wrap" aria-label="The weave — one mark per finished chore, day by day">
        <h3 class="ch-ledger-sec-h">The weave</h3>
        <p class="ch-ledger-sec-sub">
          One stitch per finished chore. Thin days are thin; empty days are empty; a hollow ring is a beat that
          was expected but missed.
        </p>
        <div class="ch-ledger-weave">
          <div class="ch-ledger-dow" aria-hidden="true">
            {#each DOW as label, i (i)}<span>{label}</span>{/each}
          </div>
          {#each weaveWeeks as week (week.weekStartLocal)}
            <div
              class="ch-ledger-wk"
              title="Week of {dayLabel(week.weekStartLocal)} — {week.completions} {week.completions === 1
                ? 'entry'
                : 'entries'}"
            >
              {#each week.cells as cell (cell.date)}
                <span
                  class="ch-ledger-cell"
                  class:future={cell.isFuture}
                  class:empty={!cell.isFuture && cell.count === 0 && !cell.hasGhost}
                  class:missed={!cell.isFuture && cell.count === 0 && cell.hasGhost}
                  class:today={cell.isToday}
                  aria-hidden="true"
                >
                  {#if cell.count > 0}
                    {#each Array.from({ length: Math.min(cell.count, MAX_CELL_DOTS) }) as _, i (i)}<i></i>{/each}
                  {/if}
                </span>
              {/each}
            </div>
          {/each}
        </div>
      </section>
    {/if}

    <!-- ── Gone quiet ───────────────────────────────────────────────────── -->
    {#if goneQuiet.length > 0}
      <section class="ch-ledger-quiet" aria-label="Gone quiet">
        <h3 class="ch-ledger-sec-h">Gone quiet</h3>
        <p class="ch-ledger-sec-sub">
          Silences in the record — chores that stopped appearing. A gap belongs to the chore, never to a person.
        </p>
        <ul class="ch-ledger-quiet-list">
          {#each goneQuiet as q (q.choreName)}
            <li class="ch-ledger-qcard">
              <div class="ch-ledger-qhead">
                <span class="ch-ledger-qname">{q.choreName}</span>
                <span class="ch-ledger-pill" class:soft={q.reason === 'slipped'}>{q.reason}</span>
              </div>
              <p class="ch-ledger-qmeta">
                {q.cadenceLabel} ·
                {#if q.lastCompletedLocalDate}
                  last written {dayLabel(q.lastCompletedLocalDate)}
                {:else}
                  never written down yet
                {/if}
              </p>
            </li>
          {/each}
        </ul>
      </section>
    {/if}

    <!-- ── The record (feed) ────────────────────────────────────────────── -->
    {#if feedWeeks.length > 0}
      <section class="ch-ledger-record" aria-label="The record — newest first">
        <h3 class="ch-ledger-sec-h">The record</h3>
        {#each feedWeeks as week (week.weekStartLocal)}
          <div class="ch-ledger-wksep">
            <span class="ch-ledger-wl">Week of {dayLabel(week.weekStartLocal)}</span>
            <span class="ch-ledger-wc">
              {week.completions} {week.completions === 1 ? 'entry' : 'entries'}
            </span>
          </div>
          {#each week.days as day (day.date)}
            <div class="ch-ledger-day">
              <div class="ch-ledger-dayh" class:is-today={day.date === today}>
                <span class="ch-ledger-dl">{day.date === today ? 'Today' : dayLabel(day.date)}</span>
              </div>
              {#each day.events as event, i (i)}
                <div class="ch-ledger-entry">
                  <span class="ch-ledger-chore">{event.choreName}</span>
                  <span class="ch-ledger-who">{event.doerDisplayName}</span>
                  <span class="ch-ledger-pts">{event.points} {event.points === 1 ? 'pt' : 'pts'}</span>
                  {#if event.hasPhoto}<span class="ch-ledger-photo" title="has a photo">📷</span>{/if}
                  {#if event.note}<p class="ch-ledger-note">{event.note}</p>{/if}
                </div>
              {/each}
              {#each day.ghosts as ghost, i (i)}
                <div class="ch-ledger-ghost">
                  <span class="ch-ledger-ghost-mark" aria-hidden="true">◌</span>
                  <span class="ch-ledger-ghost-text">
                    <b>{ghost.choreName}</b> was due around here —
                    {ghost.reason === 'snoozed'
                      ? "it's snoozed, so it isn't asking anyone"
                      : 'this beat slipped by'}.
                  </span>
                </div>
              {/each}
            </div>
          {/each}
        {/each}
        <p class="ch-ledger-colophon">
          This ledger keeps deeds, not scores. Hollow marks are questions for the house — never verdicts on a
          person.
        </p>
      </section>
    {/if}
  {:else}
    <div class="ch-ledger-state">
      <p class="ch-ledger-empty-head">The ledger is empty.</p>
      <p>It fills in as chores get completed — every deed, the moment it happens.</p>
    </div>
  {/if}
</div>

<style>
  .ch-ledger {
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  /* ── Masthead ───────────────────────────────────────────────────────────── */
  .ch-ledger-masthead {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 2px;
    padding: 20px 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
    text-align: center;
  }
  .ch-ledger-hero-num {
    font-size: 2.75rem;
    font-weight: 700;
    line-height: 1;
    color: var(--color-text);
    font-variant-numeric: tabular-nums;
  }
  .ch-ledger-hero-lab {
    font-size: 0.75rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--color-text-muted);
  }
  .ch-ledger-sub {
    margin: 10px 0 0;
    font-size: 0.8125rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.5;
    max-width: 34ch;
  }

  /* ── Section headers ────────────────────────────────────────────────────── */
  .ch-ledger-sec-h {
    margin: 0;
    font-size: 0.9375rem;
    font-weight: 600;
    color: var(--color-text);
  }
  .ch-ledger-sec-sub {
    margin: 4px 0 0;
    font-size: 0.75rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.45;
  }

  /* ── The weave ──────────────────────────────────────────────────────────── */
  .ch-ledger-weave-wrap {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-ledger-weave {
    display: flex;
    gap: 4px;
    margin-top: 12px;
    align-items: stretch;
  }
  .ch-ledger-dow {
    display: flex;
    flex-direction: column;
    gap: 3px;
    padding-right: 2px;
  }
  .ch-ledger-dow span {
    height: 16px;
    line-height: 16px;
    font-size: 0.5625rem;
    color: var(--color-text-muted);
    font-variant-numeric: tabular-nums;
  }
  .ch-ledger-wk {
    flex: 1;
    display: flex;
    flex-direction: column;
    gap: 3px;
    min-width: 0;
  }
  .ch-ledger-cell {
    height: 16px;
    border-radius: 3px;
    background: var(--color-surface);
    display: flex;
    flex-wrap: wrap;
    align-content: center;
    justify-content: center;
    gap: 1.5px;
    padding: 1px;
  }
  .ch-ledger-cell.empty {
    background: var(--color-surface);
    opacity: 0.5;
  }
  .ch-ledger-cell.future {
    visibility: hidden;
  }
  .ch-ledger-cell.missed {
    background: transparent;
    border: 1px dashed var(--color-line-strong);
  }
  .ch-ledger-cell.today {
    box-shadow: 0 0 0 1.5px var(--color-primary);
  }
  .ch-ledger-cell i {
    width: 3.5px;
    height: 3.5px;
    border-radius: 50%;
    background: var(--color-primary);
  }

  /* ── Gone quiet ─────────────────────────────────────────────────────────── */
  .ch-ledger-quiet {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding: 16px;
    background: var(--color-action-hover);
    border: 1px dashed var(--color-line-strong);
    border-radius: var(--radius-md);
  }
  .ch-ledger-quiet-list {
    list-style: none;
    margin: 12px 0 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .ch-ledger-qcard {
    display: flex;
    flex-direction: column;
    gap: 4px;
    padding-bottom: 12px;
    border-bottom: 1px dotted var(--color-line);
  }
  .ch-ledger-qcard:last-child {
    border-bottom: none;
    padding-bottom: 0;
  }
  .ch-ledger-qhead {
    display: flex;
    align-items: center;
    gap: 8px;
  }
  .ch-ledger-qname {
    font-size: 0.9375rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .ch-ledger-pill {
    font-size: 0.625rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    border: 1px solid var(--color-primary);
    color: var(--color-primary);
    border-radius: 999px;
    padding: 2px 8px;
  }
  .ch-ledger-pill.soft {
    border-color: var(--color-line-strong);
    color: var(--color-text-muted);
  }
  .ch-ledger-qmeta {
    margin: 0;
    font-size: 0.75rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.5;
  }

  /* ── The record (feed) ──────────────────────────────────────────────────── */
  .ch-ledger-record {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 16px;
    background: var(--color-action-hover);
    border-radius: var(--radius-md);
  }
  .ch-ledger-wksep {
    display: flex;
    justify-content: space-between;
    align-items: baseline;
    gap: 8px;
    margin-top: 12px;
    padding-top: 8px;
    border-top: 2px solid var(--color-line-strong);
  }
  .ch-ledger-wksep:first-of-type {
    margin-top: 4px;
  }
  .ch-ledger-wl {
    font-size: 0.75rem;
    font-weight: 600;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    color: var(--color-text);
  }
  .ch-ledger-wc {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    white-space: nowrap;
  }
  .ch-ledger-dayh {
    padding: 10px 0 2px;
  }
  .ch-ledger-dl {
    font-size: 0.6875rem;
    text-transform: uppercase;
    letter-spacing: 0.08em;
    color: var(--color-text-muted);
  }
  .ch-ledger-dayh.is-today .ch-ledger-dl {
    color: var(--color-primary);
    font-weight: 700;
  }
  .ch-ledger-entry {
    display: flex;
    align-items: baseline;
    flex-wrap: wrap;
    gap: 8px;
    padding: 6px 0;
    padding-left: 10px;
    border-left: 2px solid var(--color-line);
  }
  .ch-ledger-chore {
    font-size: 0.9375rem;
    color: var(--color-text);
  }
  .ch-ledger-who {
    font-size: 0.6875rem;
    text-transform: uppercase;
    letter-spacing: 0.06em;
    background: var(--color-surface);
    color: var(--color-text-muted);
    border-radius: 999px;
    padding: 2px 8px;
  }
  .ch-ledger-pts {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    font-variant-numeric: tabular-nums;
  }
  .ch-ledger-photo {
    font-size: 0.75rem;
  }
  .ch-ledger-note {
    flex-basis: 100%;
    margin: 4px 0 0;
    font-size: 0.8125rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.5;
  }
  /* Ghost rows — visually distinct from real entries: they are absences, framed gently. */
  .ch-ledger-ghost {
    display: flex;
    align-items: baseline;
    gap: 8px;
    padding: 6px 0 6px 10px;
    margin-top: 2px;
    border-left: 2px dashed var(--color-line-strong);
  }
  .ch-ledger-ghost-mark {
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }
  .ch-ledger-ghost-text {
    font-size: 0.8125rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.45;
  }
  .ch-ledger-ghost-text b {
    font-style: normal;
    font-weight: 500;
    color: var(--color-text);
  }
  .ch-ledger-colophon {
    margin: 16px 0 0;
    padding-top: 12px;
    border-top: 2px solid var(--color-line-strong);
    text-align: center;
    font-size: 0.75rem;
    font-style: italic;
    color: var(--color-text-muted);
    line-height: 1.6;
  }

  /* ── Loading / empty / error ────────────────────────────────────────────── */
  .ch-ledger-state {
    padding: 40px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .ch-ledger-empty-head {
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
    margin: 0 0 4px;
  }
  .ch-ledger-error {
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
  .ch-ledger-retry {
    font: inherit;
    border: none;
    cursor: pointer;
    border-radius: var(--radius-sm);
    padding: 4px 12px;
    background: transparent;
    color: var(--color-error);
    font-weight: 500;
  }
  .ch-ledger-retry:hover {
    background: rgba(229, 57, 53, 0.12);
  }
</style>
