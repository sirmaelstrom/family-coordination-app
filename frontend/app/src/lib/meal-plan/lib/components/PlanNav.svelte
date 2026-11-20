<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Meal-plan navigation for both views: prev / next chevrons (a week in week
  // view, a day in day view), the centered label (week range, or the full day
  // title), a "This week" / "Today" chip when on it, a Week | Day toggle, and
  // "Jump to Today" (disabled when already there).
  //
  // Presentational only: App turns each callback into a URL change, and the
  // URL drives the store. No `new Date('YYYY-MM-DD')` — labels come from dates.ts.
  // No clock read either: `today` is the owner's TodayClock value, so the chip
  // and "Jump to Today" move when the day does (a `todayStr()` here went stale
  // at midnight).
  // ───────────────────────────────────────────────────────────────────────
  import { weekRangeLabel, dayTitle, mondayOf } from '../dates';
  import type { PlanView } from '../view';

  interface Props {
    view: PlanView;
    /** "YYYY-MM-DD" — the day shown, or a day in the week shown. */
    date: string;
    /** "YYYY-MM-DD" — the current local day, from the owner's TodayClock. */
    today: string;
    onPrev: () => void;
    onNext: () => void;
    onToday: () => void;
    onViewChange: (view: PlanView) => void;
  }

  let { view, date, today, onPrev, onNext, onToday, onViewChange }: Props = $props();

  let isCurrent = $derived(view === 'day' ? date === today : mondayOf(date) === mondayOf(today));
  let label = $derived(view === 'day' ? dayTitle(date) : weekRangeLabel(mondayOf(date)));
  let unit = $derived(view === 'day' ? 'day' : 'week');
</script>

<nav class="mp-nav" aria-label="Meal plan navigation">
  <div class="mp-nav-header">
    <button type="button" class="mp-nav-chevron" aria-label={`Previous ${unit}`} onclick={onPrev}>
      <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
        <path d="M15.41 7.41 14 6l-6 6 6 6 1.41-1.41L10.83 12z" fill="currentColor" />
      </svg>
    </button>

    <div class="mp-nav-center">
      <h2 class="mp-nav-range">{label}</h2>
      {#if isCurrent}
        <span class="mp-nav-chip">{view === 'day' ? 'Today' : 'This week'}</span>
      {/if}
    </div>

    <button type="button" class="mp-nav-chevron" aria-label={`Next ${unit}`} onclick={onNext}>
      <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
        <path d="M10 6 8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z" fill="currentColor" />
      </svg>
    </button>
  </div>

  <div class="mp-nav-actions">
    <div class="mp-nav-toggle" role="group" aria-label="View">
      <button
        type="button"
        class="mp-nav-toggle-btn"
        aria-pressed={view === 'week'}
        onclick={() => onViewChange('week')}>Week</button
      >
      <button
        type="button"
        class="mp-nav-toggle-btn"
        aria-pressed={view === 'day'}
        onclick={() => onViewChange('day')}>Day</button
      >
    </div>
    <button
      type="button"
      class="mp-nav-today"
      onclick={onToday}
      disabled={isCurrent}
    >
      <svg viewBox="0 0 24 24" width="18" height="18" aria-hidden="true">
        <path
          d="M19 3h-1V1h-2v2H8V1H6v2H5c-1.11 0-1.99.9-1.99 2L3 19a2 2 0 0 0 2 2h14c1.1 0 2-.9 2-2V5a2 2 0 0 0-2-2zm0 16H5V8h14v11zM7 10h5v5H7z"
          fill="currentColor"
        />
      </svg>
      Jump to Today
    </button>
  </div>
</nav>

<style>
  .mp-nav {
    margin-bottom: 24px;
  }
  .mp-nav-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 16px;
  }
  .mp-nav-center {
    flex: 1;
    min-width: 0;
    text-align: center;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 4px;
  }
  .mp-nav-range {
    margin: 0;
    font-size: 1.5rem;
    font-weight: 400;
    color: var(--color-text);
  }
  .mp-nav-chip {
    display: inline-block;
    font-size: 0.75rem;
    font-weight: 500;
    color: #fff;
    background: var(--color-primary);
    border-radius: 12px;
    padding: 2px 10px;
  }
  .mp-nav-chevron {
    display: grid;
    place-items: center;
    width: 44px;
    height: 44px;
    border: none;
    border-radius: 50%;
    background: transparent;
    color: var(--color-primary);
    cursor: pointer;
    flex-shrink: 0;
    transition: background-color 0.15s;
  }
  .mp-nav-chevron:hover {
    background: var(--color-action-hover);
  }
  .mp-nav-actions {
    display: flex;
    flex-wrap: wrap;
    justify-content: center;
    align-items: center;
    gap: 12px;
    margin-top: 12px;
  }
  .mp-nav-toggle {
    display: inline-flex;
    border: 1px solid var(--color-line-strong);
    border-radius: 20px;
    overflow: hidden;
  }
  .mp-nav-toggle-btn {
    font: inherit;
    font-size: 0.875rem;
    font-weight: 500;
    min-width: 64px;
    min-height: 36px;
    padding: 6px 16px;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    cursor: pointer;
    transition: background-color 0.15s, color 0.15s;
  }
  .mp-nav-toggle-btn:hover {
    background: var(--color-action-hover);
  }
  .mp-nav-toggle-btn[aria-pressed='true'] {
    background: var(--color-primary);
    color: #fff;
  }
  .mp-nav-today {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    font: inherit;
    font-size: 0.875rem;
    font-weight: 500;
    letter-spacing: 0.02em;
    color: var(--color-primary);
    background: transparent;
    border: none;
    border-radius: var(--radius-sm);
    padding: 8px 16px;
    min-height: 40px;
    cursor: pointer;
    transition: background-color 0.15s;
  }
  .mp-nav-today:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .mp-nav-today:disabled {
    color: var(--color-text-disabled);
    cursor: not-allowed;
  }
</style>
