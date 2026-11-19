<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Desktop calendar grid (md+). A 60px label column + 7 day columns; header
  // row of weekday + date (today highlighted) — each header is a button that
  // opens that day in day view. Meal rows: Breakfast / Lunch / Dinner, plus
  // Snack when the week has a snack entry (store.weekMealRows).
  //
  // Row labels stick to the top of their row. A heavy day makes a row
  // thousands of px tall; a vertically centred label then sat off-screen and
  // the row read as unlabelled.
  // ───────────────────────────────────────────────────────────────────────
  import type { MealPlanEntryDto, MealType } from '../types';
  import { mealPlanStore } from '../state.svelte';
  import { weekDays, weekdayShort, monthDay, dayLong } from '../dates';
  import MealSlot from './MealSlot.svelte';

  interface Props {
    weekStart: string;
    /** "YYYY-MM-DD" — the current local day, from the owner's TodayClock (the highlighted column). */
    today: string;
    onSlotAdd: (date: string, mealType: MealType) => void;
    onRemove: (entry: MealPlanEntryDto) => void;
    /** Change how many this meal is cooked for (prompts in the parent). */
    onSetServings: (entry: MealPlanEntryDto) => void;
    onViewRecipe: (entry: MealPlanEntryDto) => void;
    onViewCustom: (entry: MealPlanEntryDto) => void;
    /** Open one day in day view (a day header was clicked). */
    onOpenDay: (date: string) => void;
  }

  let { weekStart, today, onSlotAdd, onRemove, onSetServings, onViewRecipe, onViewCustom, onOpenDay }: Props =
    $props();

  const store = mealPlanStore;
  let days = $derived(weekDays(weekStart));
  let rows = $derived(store.weekMealRows());

  const MEAL_LABELS: Record<MealType, string> = {
    breakfast: 'Breakfast',
    lunch: 'Lunch',
    dinner: 'Dinner',
    snack: 'Snack',
  };
</script>

<div class="mp-grid">
  <div class="mp-grid-corner"></div>
  {#each days as day (day)}
    {@const count = store.dayCount(day)}
    <button
      type="button"
      class="mp-day-header"
      class:today={day === today}
      aria-label={`Open ${dayLong(day)} in day view`}
      title="Open in day view"
      onclick={() => onOpenDay(day)}
    >
      <span class="mp-day-name">{weekdayShort(day)}</span>
      <span class="mp-day-date">{monthDay(day)}</span>
      <span class="mp-day-count">{count === 0 ? '' : count === 1 ? '1 meal' : `${count} meals`}</span>
    </button>
  {/each}

  {#each rows as mealType (mealType)}
    <div class="mp-meal-label">
      <span>{MEAL_LABELS[mealType]}</span>
    </div>
    {#each days as day (day)}
      <div class="mp-grid-cell">
        <MealSlot
          compact
          entries={store.zoneFor(day, mealType)}
          onAdd={() => onSlotAdd(day, mealType)}
          {onRemove}
          {onSetServings}
          {onViewRecipe}
          {onViewCustom}
          onDnd={(items, phase) =>
            phase === 'consider'
              ? store.zoneConsider(day, mealType, items)
              : void store.zoneFinalize(day, mealType, items)}
        />
      </div>
    {/each}
  {/each}
</div>

<style>
  .mp-grid {
    display: grid;
    grid-template-columns: 60px repeat(7, minmax(0, 1fr));
    gap: 4px;
    width: 100%;
  }
  .mp-day-header {
    text-align: center;
    padding: 8px 4px;
    min-width: 0;
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 2px;
    font: inherit;
    border: none;
    background: transparent;
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition: background-color 0.15s;
  }
  .mp-day-header:hover {
    background: var(--color-action-hover);
  }
  .mp-day-header.today {
    background: var(--color-primary);
  }
  .mp-day-header.today .mp-day-name,
  .mp-day-header.today .mp-day-date,
  .mp-day-header.today .mp-day-count {
    color: #fff;
  }
  .mp-day-name {
    font-size: 0.8125rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .mp-day-date {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  /* Entry count, so a heavy day is visible from the header row (empty days keep the line). */
  .mp-day-count {
    min-height: 1em;
    font-size: 0.6875rem;
    line-height: 1;
    color: var(--color-text-muted);
  }
  .mp-meal-label > span {
    position: sticky;
    top: 8px;
    display: block;
    margin: 0 auto;
    padding: 8px 0;
    writing-mode: vertical-rl;
    transform: rotate(180deg);
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .mp-grid-cell {
    min-width: 0;
    overflow: hidden;
  }
</style>
