<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Desktop calendar grid (md+). Mirrors the Blazor WeeklyCalendarView: a
  // 60px label column + 7 day columns; header row of weekday + date (today
  // highlighted); meal rows for Breakfast / Lunch / Dinner ONLY. Snack stays
  // implicit (the enum has it, but the grid never renders a Snack row — only
  // reachable if data carries one, matching the current page).
  // ───────────────────────────────────────────────────────────────────────
  import type { MealPlanEntryDto, MealType } from '../types';
  import { mealPlanStore, MEAL_ROWS } from '../state.svelte';
  import { weekDays, weekdayShort, monthDay, isToday } from '../dates';
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

<div class="mp-grid">
  <div class="mp-grid-corner"></div>
  {#each days as day (day)}
    <div class="mp-day-header" class:today={isToday(day)}>
      <span class="mp-day-name">{weekdayShort(day)}</span>
      <span class="mp-day-date">{monthDay(day)}</span>
    </div>
  {/each}

  {#each MEAL_ROWS as mealType (mealType)}
    <div class="mp-meal-label">
      <span>{MEAL_LABELS[mealType]}</span>
    </div>
    {#each days as day (day)}
      <div class="mp-grid-cell">
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
    gap: 2px;
    border-radius: var(--radius-sm);
  }
  .mp-day-header.today {
    background: var(--color-primary);
  }
  .mp-day-header.today .mp-day-name,
  .mp-day-header.today .mp-day-date {
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
  .mp-meal-label {
    display: flex;
    align-items: center;
    justify-content: center;
    writing-mode: vertical-rl;
    text-orientation: mixed;
    transform: rotate(180deg);
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .mp-grid-cell {
    min-width: 0;
    overflow: hidden;
  }
</style>
