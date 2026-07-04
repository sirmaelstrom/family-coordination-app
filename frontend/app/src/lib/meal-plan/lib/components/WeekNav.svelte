<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Week navigation — mirrors the Blazor MealPlanNavigation: prev / next
  // chevrons, the centered week-range label, a "This week" chip when on the
  // current week, and a "Jump to Today" button (disabled on the current week).
  //
  // Stepping is DISPLAY-ONLY (the server re-snaps the week to Monday on every
  // board GET). No `new Date('YYYY-MM-DD')` — the label comes from dates.ts.
  // ───────────────────────────────────────────────────────────────────────
  import { weekRangeLabel, todayMonday } from '../dates';

  interface Props {
    /** The week's Monday "YYYY-MM-DD". */
    weekStart: string;
    onPrev: () => void;
    onNext: () => void;
    onToday: () => void;
  }

  let { weekStart, onPrev, onNext, onToday }: Props = $props();

  let isCurrentWeek = $derived(weekStart === todayMonday());
  let rangeLabel = $derived(weekRangeLabel(weekStart));
</script>

<nav class="mp-nav" aria-label="Week navigation">
  <div class="mp-nav-header">
    <button type="button" class="mp-nav-chevron" aria-label="Previous week" onclick={onPrev}>
      <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
        <path d="M15.41 7.41 14 6l-6 6 6 6 1.41-1.41L10.83 12z" fill="currentColor" />
      </svg>
    </button>

    <div class="mp-nav-center">
      <h2 class="mp-nav-range">{rangeLabel}</h2>
      {#if isCurrentWeek}
        <span class="mp-nav-chip">This week</span>
      {/if}
    </div>

    <button type="button" class="mp-nav-chevron" aria-label="Next week" onclick={onNext}>
      <svg viewBox="0 0 24 24" width="24" height="24" aria-hidden="true">
        <path d="M10 6 8.59 7.41 13.17 12l-4.58 4.59L10 18l6-6z" fill="currentColor" />
      </svg>
    </button>
  </div>

  <div class="mp-nav-actions">
    <button
      type="button"
      class="mp-nav-today"
      onclick={onToday}
      disabled={isCurrentWeek}
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
    justify-content: center;
    margin-top: 12px;
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
