<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Recipe picker — replaces the Blazor RecipePickerDialog (MudDialog). A
  // bottom sheet from a slot's ＋, with 3 segmented modes + a shared Notes field:
  //   • Search   — debounced searchRecipes(q), image+name rows, pick one.
  //   • Custom   — a free-text meal name (+ the shared notes).
  //   • New      — name + RecipeType select → quickCreateRecipe → then add the
  //                entry with the new recipeId (two calls — mirrors today's flow).
  // On confirm the parent runs store.addEntry(...) for the open slot.
  //
  // Empty `q` ⇒ all (MinCharacters=0 parity). No date math here — the slot's
  // date/mealType are passed straight through by the parent.
  // ───────────────────────────────────────────────────────────────────────
  import type { MealRecipeSummaryDto, RecipeType } from '../types';
  import { searchRecipes, quickCreateRecipe, ApiError } from '../api';
  import { RECIPE_TYPES, recipeTypeLabel } from '../recipeType';

  type Mode = 'search' | 'custom' | 'new';

  /** What the parent needs to add the entry once the sheet confirms. */
  export interface PickerResult {
    recipeId?: number;
    customMealName?: string;
    notes?: string | null;
  }

  interface Props {
    open: boolean;
    /** A human label for the slot being filled, e.g. "Mon — Dinner". */
    slotLabel: string;
    onClose: () => void;
    onConfirm: (result: PickerResult) => void | Promise<void>;
  }

  let { open, slotLabel, onClose, onConfirm }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);

  let mode = $state<Mode>('search');
  let notes = $state('');

  // ── Search mode ──────────────────────────────────────────────────────────
  let query = $state('');
  let results = $state<MealRecipeSummaryDto[]>([]);
  let searching = $state(false);
  /** True when the last search request FAILED (vs. genuinely returned nothing) — drives an honest error row. */
  let searchError = $state(false);
  let selectedRecipe = $state<MealRecipeSummaryDto | null>(null);
  let searchToken = 0; // guards out-of-order debounced responses
  let debounceTimer: ReturnType<typeof setTimeout> | null = null;

  // ── Custom mode ──────────────────────────────────────────────────────────
  let customMealName = $state('');

  // ── New-recipe mode ──────────────────────────────────────────────────────
  let newRecipeName = $state('');
  let newRecipeType = $state<RecipeType>('main');
  let creating = $state(false);

  let localError = $state<string | null>(null);
  let submitting = $state(false);

  const MODES: { id: Mode; label: string }[] = [
    { id: 'search', label: 'Pick Recipe' },
    { id: 'custom', label: 'Custom Meal' },
    { id: 'new', label: 'New Recipe' },
  ];

  function reset(): void {
    mode = 'search';
    notes = '';
    query = '';
    results = [];
    searching = false;
    searchError = false;
    selectedRecipe = null;
    customMealName = '';
    newRecipeName = '';
    newRecipeType = 'main';
    creating = false;
    localError = null;
    submitting = false;
    if (debounceTimer) {
      clearTimeout(debounceTimer);
      debounceTimer = null;
    }
  }

  async function runSearch(q: string): Promise<void> {
    const token = ++searchToken;
    searching = true;
    searchError = false;
    try {
      const found = await searchRecipes(q);
      // Drop a stale response (a newer keystroke superseded this one).
      if (token === searchToken) results = found;
    } catch {
      // A failed search is NOT "no results" — surface it distinctly (council R1).
      if (token === searchToken) {
        results = [];
        searchError = true;
      }
    } finally {
      if (token === searchToken) searching = false;
    }
  }

  function onQueryInput(): void {
    selectedRecipe = null;
    if (debounceTimer) clearTimeout(debounceTimer);
    debounceTimer = setTimeout(() => void runSearch(query), 300);
  }

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      reset();
      dialogEl.showModal();
      // Prime the list with all recipes (MinCharacters=0 parity).
      void runSearch('');
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  let canConfirm = $derived(
    mode === 'search'
      ? selectedRecipe != null
      : mode === 'custom'
        ? customMealName.trim().length > 0
        : newRecipeName.trim().length > 0,
  );

  async function confirm(): Promise<void> {
    if (!canConfirm || submitting) return;
    localError = null;
    const trimmedNotes = notes.trim() || null;

    if (mode === 'search' && selectedRecipe) {
      submitting = true;
      try {
        await onConfirm({ recipeId: selectedRecipe.recipeId, notes: trimmedNotes });
      } finally {
        submitting = false;
      }
      return;
    }

    if (mode === 'custom') {
      const trimmed = customMealName.trim();
      if (!trimmed) return;
      submitting = true;
      try {
        await onConfirm({ customMealName: trimmed, notes: trimmedNotes });
      } finally {
        submitting = false;
      }
      return;
    }

    // mode === 'new': quick-create the recipe, then add the entry with its id.
    const name = newRecipeName.trim();
    if (!name) return;
    creating = true;
    submitting = true;
    try {
      const created = await quickCreateRecipe({ name, recipeType: newRecipeType });
      await onConfirm({ recipeId: created.recipeId, notes: trimmedNotes });
    } catch (e) {
      localError =
        e instanceof ApiError
          ? "Couldn't create that recipe — please try again."
          : "Couldn't create that recipe right now.";
    } finally {
      creating = false;
      submitting = false;
    }
  }

  function pick(recipe: MealRecipeSummaryDto): void {
    selectedRecipe = selectedRecipe?.recipeId === recipe.recipeId ? null : recipe;
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="mp-sheet">
  <div class="mp-sheet-inner">
    <header class="mp-sheet-head">
      <h2>Select Meal</h2>
      {#if slotLabel}
        <span class="mp-sheet-slot">{slotLabel}</span>
      {/if}
    </header>

    <div class="mp-sheet-body">
      <div class="mp-segmented" role="group" aria-label="Meal source">
        {#each MODES as m (m.id)}
          <button
            type="button"
            class="mp-seg"
            class:active={mode === m.id}
            aria-pressed={mode === m.id}
            onclick={() => (mode = m.id)}
          >
            {m.label}
          </button>
        {/each}
      </div>

      {#if mode === 'search'}
        <label class="mp-field">
          <span class="mp-field-label">Search recipes</span>
          <input
            type="text"
            bind:value={query}
            oninput={onQueryInput}
            autocomplete="off"
            placeholder="Type to search…"
          />
        </label>
        <div class="mp-results" role="listbox" aria-label="Recipes">
          {#if searching && results.length === 0}
            <p class="mp-results-empty">Searching…</p>
          {:else if searchError}
            <p class="mp-results-empty">Couldn't load recipes — please try again.</p>
          {:else if results.length === 0}
            <p class="mp-results-empty">No recipes found.</p>
          {:else}
            {#each results as recipe (recipe.recipeId)}
              <button
                type="button"
                class="mp-result"
                class:selected={selectedRecipe?.recipeId === recipe.recipeId}
                role="option"
                aria-selected={selectedRecipe?.recipeId === recipe.recipeId}
                onclick={() => pick(recipe)}
              >
                {#if recipe.imagePath}
                  <img src={recipe.imagePath} alt={recipe.name} class="mp-result-image" />
                {:else}
                  <span class="mp-result-placeholder" aria-hidden="true">
                    <svg viewBox="0 0 24 24" width="18" height="18">
                      <path
                        d="M11 9H9V2H7v7H5V2H3v7c0 2.12 1.66 3.84 3.75 3.97V22h2.5v-9.03C11.34 12.84 13 11.12 13 9V2h-2v7zm5-3v8h2.5v8H21V2c-2.76 0-5 2.24-5 4z"
                        fill="currentColor"
                      />
                    </svg>
                  </span>
                {/if}
                <span class="mp-result-name">{recipe.name}</span>
              </button>
            {/each}
          {/if}
        </div>
      {:else if mode === 'custom'}
        <label class="mp-field">
          <span class="mp-field-label">Custom meal name</span>
          <input
            type="text"
            bind:value={customMealName}
            autocomplete="off"
            placeholder="e.g. Leftovers, Eating out, Takeout"
          />
        </label>
      {:else}
        <p class="mp-hint">Quick-add a recipe. You can add ingredients and details later.</p>
        <label class="mp-field">
          <span class="mp-field-label">Recipe name</span>
          <input
            type="text"
            bind:value={newRecipeName}
            autocomplete="off"
            placeholder="e.g. Mom's Lasagna"
          />
        </label>
        <label class="mp-field">
          <span class="mp-field-label">Type</span>
          <select bind:value={newRecipeType} class="mp-select">
            {#each RECIPE_TYPES as t (t)}
              <option value={t}>{recipeTypeLabel(t)}</option>
            {/each}
          </select>
        </label>
      {/if}

      <label class="mp-field">
        <span class="mp-field-label">Notes (optional)</span>
        <textarea
          bind:value={notes}
          rows="2"
          placeholder="e.g. Make extra for tomorrow, use up leftover chicken"
        ></textarea>
      </label>

      {#if localError}
        <p class="mp-sheet-error" role="alert">{localError}</p>
      {/if}
    </div>

    <footer class="mp-sheet-actions">
      <button type="button" class="mp-btn-ghost" onclick={onClose} disabled={submitting}>
        Cancel
      </button>
      <button type="button" class="mp-btn-primary" onclick={confirm} disabled={!canConfirm || submitting}>
        {#if mode === 'new'}
          {creating ? 'Creating…' : 'Create & Add'}
        {:else}
          {submitting ? 'Adding…' : 'Add'}
        {/if}
      </button>
    </footer>
  </div>
</dialog>

<style>
  .mp-sheet {
    border: none;
    padding: 0;
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-4);
    width: min(520px, 100vw);
    max-height: 90vh;
    /* Bottom sheet on mobile; centered card on wide screens. */
    margin: auto auto 0;
    border-radius: var(--radius-md) var(--radius-md) 0 0;
  }
  @media (min-width: 600px) {
    .mp-sheet {
      margin: auto;
      border-radius: var(--radius-md);
    }
  }
  .mp-sheet::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .mp-sheet-inner {
    display: flex;
    flex-direction: column;
    max-height: 90vh;
  }
  .mp-sheet-head {
    display: flex;
    align-items: baseline;
    gap: 12px;
    padding: 20px 24px 8px;
  }
  .mp-sheet-head h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .mp-sheet-slot {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .mp-sheet-body {
    overflow-y: auto;
    padding: 8px 24px 16px;
    display: flex;
    flex-direction: column;
    gap: 16px;
  }

  .mp-segmented {
    display: flex;
    gap: 6px;
  }
  .mp-seg {
    flex: 1;
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    padding: 8px 10px;
    min-height: 40px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    transition: background-color 0.15s, color 0.15s, border-color 0.15s;
  }
  .mp-seg.active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: #fff;
  }
  .mp-seg:hover:not(.active) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }

  .mp-field {
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .mp-field-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text-muted);
  }
  .mp-field input[type='text'],
  .mp-select,
  .mp-field textarea {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
  }
  .mp-field textarea {
    resize: vertical;
    min-height: 60px;
  }
  .mp-field input[type='text']:focus,
  .mp-select:focus,
  .mp-field textarea:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }

  .mp-results {
    display: flex;
    flex-direction: column;
    gap: 4px;
    max-height: 280px;
    overflow-y: auto;
  }
  .mp-results-empty {
    margin: 0;
    padding: 16px 4px;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .mp-result {
    display: flex;
    align-items: center;
    gap: 12px;
    width: 100%;
    text-align: left;
    font: inherit;
    color: inherit;
    background: transparent;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm);
    padding: 8px 12px;
    min-height: 56px;
    cursor: pointer;
    transition: background-color 0.15s, border-color 0.15s;
  }
  .mp-result:hover {
    background: var(--color-action-hover);
    border-color: var(--color-line-strong);
  }
  .mp-result.selected {
    border-color: var(--color-primary);
    background: var(--color-action-hover);
  }
  .mp-result-image,
  .mp-result-placeholder {
    width: 40px;
    height: 40px;
    border-radius: var(--radius-sm);
    flex-shrink: 0;
  }
  .mp-result-image {
    object-fit: cover;
  }
  .mp-result-placeholder {
    display: grid;
    place-items: center;
    background: var(--color-action-hover);
    color: var(--color-text-muted);
  }
  .mp-result-name {
    flex: 1;
    min-width: 0;
    font-size: 0.9375rem;
    overflow: hidden;
    text-overflow: ellipsis;
    white-space: nowrap;
  }

  .mp-hint {
    margin: 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .mp-sheet-error {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-error);
  }

  .mp-sheet-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 16px 24px calc(20px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }
  .mp-btn-ghost,
  .mp-btn-primary {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
    letter-spacing: 0.02em;
  }
  .mp-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .mp-btn-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .mp-btn-primary {
    background: var(--color-primary);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .mp-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .mp-btn-primary:disabled,
  .mp-btn-ghost:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
