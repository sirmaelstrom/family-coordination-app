<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Single-day view: one section per meal type (Breakfast, Lunch, Dinner,
  // Snack — Snack always, unlike the week grid, because a full-width day has
  // room for it), each a full-width MealSlot in its `wide` card-grid layout.
  //
  // No logic of its own: the slots are the store's per-slot zones for this
  // date (the store holds the week containing it), and add / side-dessert /
  // drag / servings / view / remove go through the same MealSlot + App
  // handlers as the week views. A drag can cross sections (another meal type
  // on this day), which the store already persists as a move.
  // ───────────────────────────────────────────────────────────────────────
  import type { MealPlanEntryDto, MealType } from '../types';
  import { mealPlanStore, MEAL_TYPES } from '../state.svelte';
  import MealSlot from './MealSlot.svelte';

  interface Props {
    /** "YYYY-MM-DD" — the day shown. */
    date: string;
    onSlotAdd: (date: string, mealType: MealType) => void;
    onRemove: (entry: MealPlanEntryDto) => void;
    onSetServings: (entry: MealPlanEntryDto) => void;
    onViewRecipe: (entry: MealPlanEntryDto) => void;
    onViewCustom: (entry: MealPlanEntryDto) => void;
  }

  let { date, onSlotAdd, onRemove, onSetServings, onViewRecipe, onViewCustom }: Props = $props();

  const store = mealPlanStore;

  const MEAL_LABELS: Record<MealType, string> = {
    breakfast: 'Breakfast',
    lunch: 'Lunch',
    dinner: 'Dinner',
    snack: 'Snack',
  };
</script>

<div class="mp-dayview">
  {#each MEAL_TYPES as mealType (mealType)}
    {@const entries = store.zoneFor(date, mealType)}
    {@const count = (store.board?.entries ?? []).filter((e) => e.date === date && e.mealType === mealType)
      .length}
    <section class="mp-dv-section" aria-labelledby={`mp-dv-${mealType}`}>
      <h3 class="mp-dv-heading" id={`mp-dv-${mealType}`}>
        {MEAL_LABELS[mealType]}
        {#if count > 0}
          <span class="mp-dv-count">{count}</span>
        {/if}
      </h3>
      <MealSlot
        wide
        {entries}
        onAdd={() => onSlotAdd(date, mealType)}
        {onRemove}
        {onSetServings}
        {onViewRecipe}
        {onViewCustom}
        onDnd={(items, phase) =>
          phase === 'consider'
            ? store.zoneConsider(date, mealType, items)
            : void store.zoneFinalize(date, mealType, items)}
      />
    </section>
  {/each}
</div>

<style>
  .mp-dayview {
    display: flex;
    flex-direction: column;
    gap: 24px;
  }
  .mp-dv-heading {
    display: flex;
    align-items: center;
    gap: 8px;
    margin: 0 0 8px;
    font-size: 1.125rem;
    font-weight: 500;
    color: var(--color-text);
  }
  .mp-dv-count {
    font-size: 0.75rem;
    font-weight: 500;
    line-height: 1;
    padding: 3px 8px;
    border-radius: 10px;
    color: var(--color-text-muted);
    border: 1px solid var(--color-line);
  }
</style>
