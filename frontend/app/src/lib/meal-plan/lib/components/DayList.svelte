<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Mobile day list (sm-). Mirrors the Blazor WeeklyListView: one expandable
  // section per day (native <details>), with the weekday + date, a "Today"
  // chip, and a "N meals" count in the summary; expanding shows B/L/D rows.
  // Today's section is expanded by default and left-accented.
  // ───────────────────────────────────────────────────────────────────────
  import type { MealPlanEntryDto, MealType } from '../types';
  import { mealPlanStore, MEAL_ROWS } from '../state.svelte';
  import { weekDays, weekdayLong, monthDay, isToday } from '../dates';
  import MealSlot from './MealSlot.svelte';

  interface Props {
    weekStart: string;
    onSlotAdd: (date: string, mealType: MealType) => void;
    onRemove: (entry: MealPlanEntryDto) => void;
    onViewRecipe: (entry: MealPlanEntryDto) => void;
    onViewCustom: (entry: MealPlanEntryDto) => void;
  }

  let { weekStart, onSlotAdd, onRemove, onViewRecipe, onViewCustom }: Props = $props();

  const store = mealPlanStore;
  let days = $derived(weekDays(weekStart));

  const MEAL_LABELS: Record<MealType, string> = {
    breakfast: 'Breakfast',
    lunch: 'Lunch',
    dinner: 'Dinner',
    snack: 'Snack',
  };
</script>

<div class="mp-daylist">
  {#each days as day (day)}
    {@const today = isToday(day)}
    <details class="mp-day-panel" class:today open={today}>
      <summary class="mp-day-summary">
        <span class="mp-day-summary-main">
          <span class="mp-day-summary-name">{weekdayLong(day)}</span>
          <span class="mp-day-summary-date">{monthDay(day)}</span>
        </span>
        {#if today}
          <span class="mp-day-summary-chip">Today</span>
        {/if}
        <span class="mp-day-summary-count">{store.dayCount(day)} meals</span>
      </summary>
      <div class="mp-day-body">
        {#each MEAL_ROWS as mealType (mealType)}
          <div class="mp-day-row">
            <span class="mp-day-row-label">{MEAL_LABELS[mealType]}</span>
            <div class="mp-day-row-slot">
              <MealSlot
                entries={store.zoneFor(day, mealType)}
                onAdd={() => onSlotAdd(day, mealType)}
                {onRemove}
                {onViewRecipe}
                {onViewCustom}
                onDnd={(items, phase) =>
                  phase === 'consider'
                    ? store.zoneConsider(day, mealType, items)
                    : void store.zoneFinalize(day, mealType, items)}
              />
            </div>
          </div>
        {/each}
      </div>
    </details>
  {/each}
</div>

<style>
  .mp-daylist {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .mp-day-panel {
    background: var(--color-surface);
    border-radius: var(--radius-sm);
    box-shadow: var(--shadow-1);
    overflow: hidden;
  }
  .mp-day-panel.today {
    border-left: 3px solid var(--color-primary);
  }
  .mp-day-summary {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 14px 16px;
    cursor: pointer;
    list-style: none;
  }
  .mp-day-summary::-webkit-details-marker {
    display: none;
  }
  .mp-day-summary-main {
    display: flex;
    flex-direction: column;
    gap: 2px;
    flex: 1;
    min-width: 0;
  }
  .mp-day-summary-name {
    font-size: 1rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .mp-day-summary-date {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .mp-day-summary-chip {
    font-size: 0.6875rem;
    font-weight: 500;
    color: #fff;
    background: var(--color-primary);
    border-radius: 12px;
    padding: 2px 10px;
    flex-shrink: 0;
  }
  .mp-day-summary-count {
    font-size: 0.75rem;
    color: var(--color-text-muted);
    flex-shrink: 0;
  }
  .mp-day-body {
    display: flex;
    flex-direction: column;
    gap: 8px;
    padding: 0 16px 14px;
  }
  .mp-day-row {
    display: flex;
    align-items: flex-start;
    gap: 12px;
  }
  .mp-day-row-label {
    width: 80px;
    flex-shrink: 0;
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text-muted);
    padding-top: 8px;
  }
  .mp-day-row-slot {
    flex: 1;
    min-width: 0;
  }
</style>
