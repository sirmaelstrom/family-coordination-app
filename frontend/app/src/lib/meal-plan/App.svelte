<script lang="ts">
  import { untrack } from 'svelte';
  import type { MealPlanEntryDto, MealType, ShellContext } from './lib/types';
  import { mealPlanStore } from './lib/state.svelte';
  import { startLiveness, type LivenessHandle } from './lib/liveness';
  import { weekdayShort, monthDay, todayMonday } from './lib/dates';
  import WeekNav from './lib/components/WeekNav.svelte';
  import CalendarGrid from './lib/components/CalendarGrid.svelte';
  import DayList from './lib/components/DayList.svelte';
  import RecipePickerSheet, { type PickerResult } from './lib/components/RecipePickerSheet.svelte';
  import RecipeDetailSheet from './lib/components/RecipeDetailSheet.svelte';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';

  // ───────────────────────────────────────────────────────────────────────
  // Root of the meal-plan island. Reads the ShellContext from #meal-plan-root
  // data-attrs, fetches the current week's board into the shared store, and
  // renders WeekNav + (CalendarGrid on md+ / DayList on sm-). Every slot is a
  // client-side grouping of the one board payload (store.entriesByDayMeal).
  // Versionless — add + remove only; on any 4xx/network the store reconciles.
  // No `new Date('YYYY-MM-DD')` anywhere — week stepping goes through dates.ts.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  const store = mealPlanStore;

  let liveness: LivenessHandle | null = null;

  // Responsive split mirrors the Blazor MudHidden breakpoint (Sm = 960px). We
  // watch matchMedia rather than relying on CSS alone so we only mount one tree.
  let isDesktop = $state(true);
  let mediaQuery: MediaQueryList | null = null;
  function syncMedia(): void {
    if (mediaQuery) isDesktop = mediaQuery.matches;
  }

  // ── Picker (add) state ────────────────────────────────────────────────────
  let pickerOpen = $state(false);
  let pickerSlot = $state<{ date: string; mealType: MealType } | null>(null);
  let pickerSlotLabel = $derived(
    pickerSlot
      ? `${weekdayShort(pickerSlot.date)} ${monthDay(pickerSlot.date)} — ${mealLabel(pickerSlot.mealType)}`
      : '',
  );

  // ── Detail / custom-meal view state ───────────────────────────────────────
  let detailOpen = $state(false);
  let detailMode = $state<'recipe' | 'custom'>('recipe');
  let detailRecipeId = $state<number | null>(null);
  let detailRecipeName = $state('');
  let detailCustomEntry = $state<MealPlanEntryDto | null>(null);

  // ── Remove-confirm state ──────────────────────────────────────────────────
  let confirmOpen = $state(false);
  let confirmEntry = $state<MealPlanEntryDto | null>(null);
  let confirmMessage = $derived(
    confirmEntry ? `Remove this meal from ${monthDay(confirmEntry.date)}?` : '',
  );

  function mealLabel(m: MealType): string {
    return m.charAt(0).toUpperCase() + m.slice(1);
  }

  async function loadBoard(): Promise<void> {
    await store.loadBoard();
  }

  // ── Slot handlers ──────────────────────────────────────────────────────────
  function handleSlotAdd(date: string, mealType: MealType): void {
    pickerSlot = { date, mealType };
    pickerOpen = true;
  }

  async function handlePickerConfirm(result: PickerResult): Promise<void> {
    const slot = pickerSlot;
    if (!slot) return;
    pickerOpen = false;
    pickerSlot = null;
    await store.addEntry({
      date: slot.date,
      mealType: slot.mealType,
      recipeId: result.recipeId ?? null,
      customMealName: result.customMealName ?? null,
      notes: result.notes ?? null,
    });
  }

  function handleViewRecipe(entry: MealPlanEntryDto): void {
    if (!entry.recipe) return;
    detailMode = 'recipe';
    detailRecipeId = entry.recipe.recipeId;
    detailRecipeName = entry.recipe.name;
    detailCustomEntry = null;
    detailOpen = true;
  }

  function handleViewCustom(entry: MealPlanEntryDto): void {
    detailMode = 'custom';
    detailCustomEntry = entry;
    detailRecipeId = null;
    detailOpen = true;
  }

  function handleRemove(entry: MealPlanEntryDto): void {
    confirmEntry = entry;
    confirmOpen = true;
  }

  async function handleConfirmRemove(): Promise<void> {
    const entry = confirmEntry;
    confirmOpen = false;
    confirmEntry = null;
    if (entry) await store.removeEntry(entry.mealPlanId, entry.entryId);
  }

  $effect(() => {
    // One-time mount setup. MUST run exactly once — `loadBoard()` synchronously
    // reads `store.board`/`store.weekStart` (the spinner-on-first-load check), and
    // its async `setBoard` REWRITES them, so WITHOUT untrack the effect would
    // subscribe to the very state it updates → an infinite fetch→setBoard→re-run
    // loop (~tens of req/s). `untrack` gives the body no reactive deps, so it runs
    // once; liveness + the matchMedia listener drive all later refreshes.
    untrack(() => {
      store.init(ctx);
      store.setRefresh(loadBoard);

      mediaQuery = window.matchMedia('(min-width: 960px)');
      syncMedia();
      mediaQuery.addEventListener('change', syncMedia);

      loadBoard();
      // Liveness: ~20s poll while visible + immediate refetch on refocus; pauses
      // while hidden. NOT Blazor DataNotifier/PollingService (MN2).
      liveness = startLiveness(() => store.reconcile());
    });

    return () => {
      liveness?.stop();
      liveness = null;
      mediaQuery?.removeEventListener('change', syncMedia);
      mediaQuery = null;
    };
  });
</script>

<div class="mp-container">
  <header class="mp-header">
    <h1 class="mp-title">Meal Plan</h1>
    <span class="mp-user">{ctx.userName}</span>
  </header>

  <WeekNav
    weekStart={store.weekStart}
    onPrev={() => store.changeWeek(-1)}
    onNext={() => store.changeWeek(1)}
    onToday={() => store.changeWeek(todayMonday())}
  />

  {#if store.error}
    <div class="mp-inline-error" role="alert">
      <span>{store.error}</span>
      <button type="button" class="mp-retry" onclick={loadBoard}>Retry</button>
    </div>
  {/if}

  {#if store.loading && !store.board}
    <div class="mp-loading">Loading the meal plan…</div>
  {:else if store.board}
    {#if isDesktop}
      <CalendarGrid
        weekStart={store.weekStart}
        onSlotAdd={handleSlotAdd}
        onRemove={handleRemove}
        onViewRecipe={handleViewRecipe}
        onViewCustom={handleViewCustom}
      />
    {:else}
      <DayList
        weekStart={store.weekStart}
        onSlotAdd={handleSlotAdd}
        onRemove={handleRemove}
        onViewRecipe={handleViewRecipe}
        onViewCustom={handleViewCustom}
      />
    {/if}
  {:else if !store.loading}
    <div class="mp-empty">No meal plan data.</div>
  {/if}
</div>

<RecipePickerSheet
  open={pickerOpen}
  slotLabel={pickerSlotLabel}
  onClose={() => {
    pickerOpen = false;
    pickerSlot = null;
  }}
  onConfirm={handlePickerConfirm}
/>

<RecipeDetailSheet
  open={detailOpen}
  mode={detailMode}
  recipeId={detailRecipeId}
  recipeName={detailRecipeName}
  customEntry={detailCustomEntry}
  onClose={() => (detailOpen = false)}
/>

<ConfirmDialog
  open={confirmOpen}
  title="Remove Meal"
  message={confirmMessage}
  onCancel={() => {
    confirmOpen = false;
    confirmEntry = null;
  }}
  onConfirm={handleConfirmRemove}
/>

<style>
  .mp-container {
    max-width: 1280px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .mp-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 16px;
  }
  .mp-title {
    margin: 0;
    font-size: 2.125rem;
    font-weight: 400;
    color: var(--color-text);
  }
  .mp-user {
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }
  .mp-loading,
  .mp-empty {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .mp-inline-error {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: 10px 16px;
    margin-bottom: 16px;
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    border-radius: var(--radius-sm);
    color: var(--color-error);
    font-size: 0.875rem;
  }
  .mp-retry {
    font: inherit;
    border: none;
    cursor: pointer;
    border-radius: var(--radius-sm);
    padding: 4px 12px;
    background: transparent;
    color: var(--color-error);
    font-weight: 500;
  }
  .mp-retry:hover {
    background: rgba(229, 57, 53, 0.12);
  }
</style>
