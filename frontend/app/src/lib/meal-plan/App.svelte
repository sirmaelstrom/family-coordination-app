<script lang="ts">
  import { untrack } from 'svelte';
  import { page } from '$app/state';
  import { goto } from '$app/navigation';
  import { resolve } from '$app/paths';
  import type { MealPlanEntryDto, MealType, ShellContext } from './lib/types';
  import { mealPlanStore } from './lib/state.svelte';
  import { startLiveness, type LivenessHandle } from '#lib/liveness.ts';
  import { weekdayShort, monthDay } from './lib/dates';
  import { TodayClock } from './lib/today.svelte';
  import {
    parsePlanLocation,
    planSearch,
    planWeek,
    boardIsForWeek,
    stepPlan,
    type PlanLocation,
    type PlanView,
  } from './lib/view';
  import { parseServingsInput } from './lib/servings';
  import PlanNav from './lib/components/PlanNav.svelte';
  import CalendarGrid from './lib/components/CalendarGrid.svelte';
  import DayList from './lib/components/DayList.svelte';
  import DayView from './lib/components/DayView.svelte';
  import RecipePickerSheet, { type PickerResult } from './lib/components/RecipePickerSheet.svelte';
  import RecipeDetailSheet from './lib/components/RecipeDetailSheet.svelte';
  import ConfirmDialog from '#lib/shared/ConfirmDialog.svelte';
  import PromptDialog from '#lib/shared/PromptDialog.svelte';
  import { showToast } from '#lib/shared/toast-store.svelte.ts';

  // ───────────────────────────────────────────────────────────────────────
  // Root of the meal-plan surface. The URL says where we are looking
  // (lib/view.ts: `?view=day&date=…`), the store holds the week containing
  // that date, and we render PlanNav + either DayView (one day, full width) or
  // the week (CalendarGrid on md+ / DayList on sm-). Every slot is a
  // client-side grouping of the one week payload (the store's per-slot zones),
  // so day view needs no API of its own.
  //
  // Bare `/meal-plan` is today in the screen's default view: week on md+, DAY
  // on a phone, where the week list leaves a recipe name a few characters.
  // Every other URL names its view, so links mean the same on every screen.
  // Move / servings / remove send the entry's xmin token and 409 on a stale one; add creates the row
  // so it has none. On any 4xx the store reconciles.
  // No `new Date('YYYY-MM-DD')` anywhere — week stepping goes through dates.ts.
  // ───────────────────────────────────────────────────────────────────────

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  const store = mealPlanStore;

  // Responsive split mirrors the Blazor MudHidden breakpoint (Sm = 960px). We
  // watch matchMedia rather than relying on CSS alone so we only mount one tree.
  // Read synchronously (SPA, no SSR) so the first render already has the right
  // default view — no week-then-day flash on a phone.
  const DESKTOP_QUERY = '(min-width: 960px)';
  let isDesktop = $state(window.matchMedia(DESKTOP_QUERY).matches);
  let mediaQuery: MediaQueryList | null = null;
  function syncMedia(): void {
    if (mediaQuery) isDesktop = mediaQuery.matches;
  }

  // ── Location (URL-owned) ──────────────────────────────────────────────────
  // Read straight from the URL; every navigation below is a goto() to the same
  // route with a new query, so back/forward and reload all land where the URL says.
  let defaultView = $derived<PlanView>(isDesktop ? 'week' : 'day');
  // Today is reactive (lib/today.svelte.ts): it moves at midnight and when the page wakes, so the Today
  // chip, Jump to Today and the week views' today marks (all handed `clock.value`) leave yesterday when
  // the day ends. The WEEK on screen never moves by itself: a bare `/meal-plan` reads its day once per
  // navigation (`clock.linkDay(page.url)`), and a URL that names its date stays on that date. So after
  // midnight the page stays where the member left it, with Jump to Today enabled, and only the member
  // (a click, a link, Back/Forward, a reload) changes the week. Nothing open (a sheet, a dialog, the
  // picker, a drag) can have its week change under it (design note fca-175-no-switch-2026-11-20).
  const clock = new TodayClock();
  let loc = $derived(parsePlanLocation(page.url.searchParams, clock.linkDay(page.url), defaultView));
  /** True only when the URL's week, the store's week and the board in hand agree (the render gate). */
  let boardForWeek = $derived(boardIsForWeek(store.board, store.weekStart, planWeek(loc)));

  /**
   * Go to a location. Switching view pushes history (Back returns to the week);
   * stepping prev/next/today replaces it, so Back isn't a day-by-day replay.
   *
   * Kit 3's one `reset` flag moves scroll AND focus, and it also decides whether
   * Back restores the scroll. A step keeps both (`reset: false`). A view switch
   * resets, so the day opens at its top (a header tapped far down the phone week
   * list would otherwise land mid-day) and Back returns to the week where it was.
   * Then focus goes back to the control that had it (the Week/Day toggle stays
   * mounted), so a keyboard user isn't thrown to the top of the page.
   */
  async function go(next: PlanLocation, opts: { replace: boolean }): Promise<void> {
    const focused = document.activeElement;
    await goto(resolve('/meal-plan') + planSearch(next), {
      replace: opts.replace,
      reset: !opts.replace,
    });
    if (!opts.replace && focused instanceof HTMLElement && focused.isConnected) {
      focused.focus({ preventScroll: true });
    }
  }

  function openDay(date: string): void {
    void go({ view: 'day', date }, { replace: false });
  }

  function jumpToToday(): void {
    clock.sync(); // the click is its own clock read, so the target can't lag the wall clock
    void go({ view: loc.view, date: clock.value }, { replace: true });
  }

  let liveness: LivenessHandle | null = null;
  let stopClock: (() => void) | null = null;

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

  // ── Servings-override state ───────────────────────────────────────────────
  let servingsOpen = $state(false);
  let servingsEntry = $state<MealPlanEntryDto | null>(null);
  let servingsLabel = $derived(
    servingsEntry?.recipe?.servings != null && servingsEntry.recipe.servings > 0
      ? `Cooking for how many? (the recipe serves ${servingsEntry.recipe.servings}) — blank to cook it as written`
      : servingsEntry?.recipe
        ? 'Cooking for how many? (the recipe has no yield set, so this scales nothing) — blank to clear it'
        : 'Cooking for how many?',
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
    if (entry) await store.removeEntry(entry.mealPlanId, entry.entryId, entry.version);
  }

  function handleSetServings(entry: MealPlanEntryDto): void {
    servingsEntry = entry;
    servingsOpen = true;
  }

  /**
   * Blank clears the override (back to the recipe as written) — which is why the dialog is opened with
   * `allowEmpty`. Parsing lives in lib/servings.ts so the empty case is testable; the server validates
   * independently.
   */
  async function handleSubmitServings(value: string): Promise<void> {
    const entry = servingsEntry;
    if (!entry) return;

    const parsed = parseServingsInput(value);
    if (!parsed.ok) {
      showToast({ message: parsed.message, kind: 'error' });
      return;
    }

    servingsOpen = false;
    servingsEntry = null;
    await store.setEntryServings(entry.mealPlanId, entry.entryId, parsed.servings, entry.version);
  }

  // Rebuild the per-slot dnd zones whenever the board data or week changes —
  // but NEVER mid-drag: while `dragActive`, the store's consider/finalize
  // handlers own the zones, and a rebuild would re-bucket the dragged node and
  // abort a cross-slot drop (the shopping-list lesson). On finalize the store
  // applies the optimistic move to `board.entries` BEFORE releasing the gate,
  // so this fires once and reconciles from the final state. $effect.pre so the
  // zones exist in the same frame the board arrives (no empty flash).
  $effect.pre(() => {
    if (store.dragActive) return;
    store.rebuildZones();
  });

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

      // The store outlives the route (module singleton): if it still holds another week from an
      // earlier visit, drop that board so the first paint is the spinner, not last visit's week.
      const monday = planWeek(loc);
      if (store.weekStart !== monday) {
        store.weekStart = monday;
        store.board = null;
      }

      mediaQuery = window.matchMedia(DESKTOP_QUERY);
      syncMedia();
      mediaQuery.addEventListener('change', syncMedia);

      loadBoard();
      // Liveness: ~20s poll while visible + immediate refetch on refocus; pauses
      // while hidden. NOT Blazor DataNotifier/PollingService (MN2).
      // Each tick also re-reads the day: a phone that slept through midnight ran no
      // timer, and the tick on re-show is the first code to run when it wakes.
      stopClock = clock.start();
      liveness = startLiveness(() => {
        clock.sync();
        void store.reconcile();
      });
    });

    return () => {
      stopClock?.();
      stopClock = null;
      liveness?.stop();
      liveness = null;
      mediaQuery?.removeEventListener('change', syncMedia);
      mediaQuery = null;
    };
  });

  // The URL moved to a date in another week (prev/next, Jump to Today, a header click near a week
  // edge, back/forward): load that week. Declared after the mount effect, so on mount it finds the
  // week already set and does nothing. Reads the store untracked: it reacts to the URL only.
  $effect(() => {
    const monday = planWeek(loc);
    untrack(() => {
      if (store.weekStart !== monday) void store.changeWeek(monday);
    });
  });
</script>

<div class="mp-container">
  <header class="mp-header">
    <h1 class="mp-title">Meal Plan</h1>
    <span class="mp-user">{ctx.userName}</span>
  </header>

  <PlanNav
    view={loc.view}
    date={loc.date}
    today={clock.value}
    onPrev={() => go(stepPlan(loc, -1), { replace: true })}
    onNext={() => go(stepPlan(loc, 1), { replace: true })}
    onToday={jumpToToday}
    onViewChange={(view) => go({ view, date: loc.date }, { replace: false })}
  />

  {#if store.error}
    <div class="mp-inline-error" role="alert">
      <span>{store.error}</span>
      <button type="button" class="mp-retry" onclick={loadBoard}>Retry</button>
    </div>
  {/if}

  <!--
    Slots render only from a board FOR the week on screen: the URL's week, the store's week and the
    board must agree (boardIsForWeek). changeWeek moves weekStart at once and keeps the old board until
    the new one arrives, so rendering "any board" showed the new week's days as empty, clickable slots
    backed by the old week, and kept showing them if the fetch failed. The store follows the URL one
    effect late, so the URL's week is part of the gate too: the template is right on its own, before
    that effect runs. Anything else means a fetch is in flight (loading text) or failed (the error
    banner's Retry above).
  -->
  {#if boardForWeek}
    {#if loc.view === 'day'}
      <DayView
        date={loc.date}
        onSlotAdd={handleSlotAdd}
        onRemove={handleRemove}
        onSetServings={handleSetServings}
        onViewRecipe={handleViewRecipe}
        onViewCustom={handleViewCustom}
      />
    {:else if isDesktop}
      <CalendarGrid
        weekStart={store.weekStart}
        today={clock.value}
        onSlotAdd={handleSlotAdd}
        onRemove={handleRemove}
        onSetServings={handleSetServings}
        onViewRecipe={handleViewRecipe}
        onViewCustom={handleViewCustom}
        onOpenDay={openDay}
      />
    {:else}
      <DayList
        weekStart={store.weekStart}
        today={clock.value}
        onSlotAdd={handleSlotAdd}
        onRemove={handleRemove}
        onSetServings={handleSetServings}
        onViewRecipe={handleViewRecipe}
        onViewCustom={handleViewCustom}
        onOpenDay={openDay}
      />
    {/if}
  {:else if store.error}
    <!-- Nothing to show for this week; the error banner above carries Retry. -->
  {:else if store.loading || store.board}
    <div class="mp-loading">Loading the meal plan…</div>
  {:else}
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

<PromptDialog
  open={servingsOpen}
  title="Servings"
  label={servingsLabel}
  initial={servingsEntry?.servings != null ? String(servingsEntry.servings) : ''}
  confirmLabel="Save"
  allowEmpty
  onClose={() => {
    servingsOpen = false;
    servingsEntry = null;
  }}
  onSubmit={handleSubmitServings}
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
