<script lang="ts">
  // Today's meals card — parity with Home.razor lines 158-206. Meals are
  // pre-grouped by meal type (server-ordered); each group shows its meals.
  import type { MealType } from '../types';
  import type { MealGroup } from '../dashboardStore.svelte';

  let { groups, dateLabel }: { groups: MealGroup[]; dateLabel: string } = $props();

  // Parity with Home.razor GetMealTypeIcon (Material icons → emoji equivalents).
  const MEAL_ICONS: Record<MealType, string> = {
    breakfast: '🍳',
    lunch: '🥪',
    dinner: '🍽️',
    snack: '🍦',
  };

  const label = (t: MealType) => t.charAt(0).toUpperCase() + t.slice(1);
</script>

<div class="db-card">
  <div class="db-card-header">
    <div class="db-card-avatar" aria-hidden="true">📅</div>
    <div class="db-card-headings">
      <h2 class="db-card-title">Today's Meals</h2>
      <p class="db-card-status">{dateLabel}</p>
    </div>
    <a class="db-card-arrow" href="/meal-plan" aria-label="Go to meal plan">→</a>
  </div>

  <div class="db-card-body">
    {#if groups.length === 0}
      <p class="db-empty">No meals planned for today</p>
      <div class="db-empty-action">
        <a class="db-link" href="/meal-plan">Plan something</a>
      </div>
    {:else}
      {#each groups as group (group.mealType)}
        <div class="db-meal-group">
          <div class="db-meal-group-head">
            <span aria-hidden="true">{MEAL_ICONS[group.mealType]}</span>
            <span>{label(group.mealType)}</span>
          </div>
          {#each group.meals as meal, i (i)}
            <div class="db-meal-item">{meal.displayName}</div>
          {/each}
        </div>
      {/each}
    {/if}
  </div>
</div>
