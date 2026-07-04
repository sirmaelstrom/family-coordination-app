<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Recipe detail (read-only) — replaces the Blazor RecipeDetailDialog. Also
  // hosts the custom-meal notes view (the Blazor page used a MessageBox for
  // that; we fold it in here per the spec). Two modes:
  //   • mode='recipe'  → lazy getRecipeDetail(recipeId): image, prep/cook/
  //     servings chips, ingredients (qty unit name (notes)), instructions via
  //     {@html} (server-sanitized — NO client markdown lib).
  //   • mode='custom'  → the entry's name + notes + date/meal context.
  // ───────────────────────────────────────────────────────────────────────
  import type { MealPlanEntryDto, RecipeDetailDto, RecipeIngredientDto } from '../types';
  import { getRecipeDetail, ApiError } from '../api';
  import { dayLong } from '../dates';

  type Mode = 'recipe' | 'custom';

  interface Props {
    open: boolean;
    mode: Mode;
    /** The recipe id to load (mode='recipe'). */
    recipeId: number | null;
    /** Fallback title while the recipe loads (the entry's recipe name). */
    recipeName: string;
    /** The custom-meal entry (mode='custom'). */
    customEntry: MealPlanEntryDto | null;
    onClose: () => void;
  }

  let { open, mode, recipeId, recipeName, customEntry, onClose }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);
  let detail = $state<RecipeDetailDto | null>(null);
  let loading = $state(false);
  let error = $state<string | null>(null);

  // Track the id we last fetched so re-opening the same recipe reuses it, but a
  // different recipe refetches. Reset on a custom-mode open.
  let loadedId = $state<number | null>(null);

  async function load(id: number): Promise<void> {
    loading = true;
    error = null;
    try {
      detail = await getRecipeDetail(id);
      loadedId = id;
    } catch (e) {
      error =
        e instanceof ApiError
          ? `Couldn't load this recipe (HTTP ${e.status}).`
          : "Couldn't load this recipe right now.";
    } finally {
      loading = false;
    }
  }

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      if (mode === 'recipe' && recipeId != null && recipeId !== loadedId) {
        detail = null;
        void load(recipeId);
      }
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  function formatIngredient(ing: RecipeIngredientDto): string {
    const parts: string[] = [];
    if (ing.quantity != null) {
      // Trim trailing zeros (mirrors the Blazor "0.##").
      parts.push(String(Number(ing.quantity.toFixed(2))));
    }
    if (ing.unit) parts.push(ing.unit);
    parts.push(ing.name);
    if (ing.notes) parts.push(`(${ing.notes})`);
    return parts.join(' ');
  }

  let title = $derived(
    mode === 'custom' ? (customEntry?.customMealName ?? 'Custom meal') : (detail?.name ?? recipeName),
  );
  let hasMeta = $derived(
    detail != null &&
      (detail.prepTimeMinutes != null ||
        detail.cookTimeMinutes != null ||
        detail.servings != null),
  );
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="mp-dialog">
  {#if open}
    <header class="mp-dialog-head">
      <h2>{title}</h2>
      <button type="button" class="mp-dialog-x" aria-label="Close" onclick={onClose}>
        <svg viewBox="0 0 24 24" width="20" height="20" aria-hidden="true">
          <path
            d="M19 6.41 17.59 5 12 10.59 6.41 5 5 6.41 10.59 12 5 17.59 6.41 19 12 13.41 17.59 19 19 17.59 13.41 12z"
            fill="currentColor"
          />
        </svg>
      </button>
    </header>

    <div class="mp-dialog-body">
      {#if mode === 'custom'}
        <div class="mp-custom-view">
          {#if customEntry?.notes}
            <div class="mp-custom-notes">
              <span class="mp-custom-label">Notes</span>
              <p class="mp-custom-notes-text">{customEntry.notes}</p>
            </div>
          {/if}
          {#if customEntry}
            <p class="mp-custom-context">{dayLong(customEntry.date)}</p>
          {/if}
          {#if !customEntry?.notes}
            <p class="mp-empty-note">No notes for this meal.</p>
          {/if}
        </div>
      {:else if loading}
        <div class="mp-detail-loading">Loading recipe…</div>
      {:else if error}
        <div class="mp-detail-error" role="alert">
          <span>{error}</span>
          {#if recipeId != null}
            <button type="button" class="mp-retry" onclick={() => recipeId != null && load(recipeId)}>
              Retry
            </button>
          {/if}
        </div>
      {:else if detail}
        {#if detail.imagePath}
          <img src={detail.imagePath} alt={detail.name} class="mp-detail-image" />
        {/if}

        {#if hasMeta}
          <div class="mp-detail-chips">
            {#if detail.prepTimeMinutes != null}
              <span class="mp-detail-chip">Prep: {detail.prepTimeMinutes} min</span>
            {/if}
            {#if detail.cookTimeMinutes != null}
              <span class="mp-detail-chip">Cook: {detail.cookTimeMinutes} min</span>
            {/if}
            {#if detail.servings != null}
              <span class="mp-detail-chip">Servings: {detail.servings}</span>
            {/if}
          </div>
        {/if}

        {#if detail.ingredients.length > 0}
          <h3 class="mp-detail-subhead">Ingredients</h3>
          <ul class="mp-detail-ingredients">
            {#each detail.ingredients as ing (ing.sortOrder)}
              <li>{formatIngredient(ing)}</li>
            {/each}
          </ul>
        {/if}

        {#if detail.instructionsHtml.trim()}
          <h3 class="mp-detail-subhead">Instructions</h3>
          <!-- Server-sanitized HTML (MarkdownHelper.ToSafeHtml). No client markdown lib. -->
          <div class="mp-detail-instructions">{@html detail.instructionsHtml}</div>
        {/if}
      {/if}
    </div>

    <footer class="mp-dialog-actions">
      <button type="button" class="mp-btn-ghost" onclick={onClose}>Close</button>
    </footer>
  {/if}
</dialog>

<style>
  /* Gate layout to [open] so the closed dialog never paints a click-eating overlay. */
  .mp-dialog[open] {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(620px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
    display: flex;
    flex-direction: column;
  }
  .mp-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .mp-dialog-head {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 16px 16px 12px 24px;
    border-bottom: 1px solid var(--color-line);
  }
  .mp-dialog-head h2 {
    margin: 0;
    flex: 1;
    min-width: 0;
    font-size: 1.25rem;
    font-weight: 500;
    overflow-wrap: anywhere;
  }
  .mp-dialog-x {
    flex-shrink: 0;
    display: grid;
    place-items: center;
    width: 36px;
    height: 36px;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    cursor: pointer;
  }
  .mp-dialog-x:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .mp-dialog-body {
    overflow-y: auto;
    padding: 16px 24px;
    flex: 1;
  }
  .mp-dialog-actions {
    display: flex;
    justify-content: flex-end;
    padding: 12px 24px calc(16px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }

  .mp-detail-image {
    width: 100%;
    height: 200px;
    object-fit: cover;
    border-radius: var(--radius-sm);
    margin-bottom: 16px;
  }
  .mp-detail-chips {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    margin-bottom: 16px;
  }
  .mp-detail-chip {
    font-size: 0.8125rem;
    padding: 4px 12px;
    border-radius: 14px;
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .mp-detail-subhead {
    margin: 16px 0 8px;
    font-size: 1rem;
    font-weight: 500;
  }
  .mp-detail-ingredients {
    margin: 0;
    padding-left: 1.25rem;
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: 0.9375rem;
  }
  .mp-detail-instructions {
    line-height: 1.6;
    font-size: 0.9375rem;
  }
  .mp-detail-instructions :global(ol),
  .mp-detail-instructions :global(ul) {
    margin-left: 1.5rem;
    margin-bottom: 1rem;
  }
  .mp-detail-instructions :global(li) {
    margin-bottom: 0.5rem;
  }
  .mp-detail-instructions :global(p) {
    margin: 0 0 0.75rem;
  }

  .mp-detail-loading {
    padding: 32px 0;
    text-align: center;
    color: var(--color-text-muted);
  }
  .mp-detail-error {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 12px 16px;
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

  /* ── Custom-meal notes view ──────────────────────────────────────────── */
  .mp-custom-view {
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  .mp-custom-notes {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .mp-custom-label {
    font-size: 0.6875rem;
    text-transform: uppercase;
    letter-spacing: 0.04em;
    color: var(--color-text-muted);
  }
  .mp-custom-notes-text {
    margin: 0;
    font-size: 0.9375rem;
    line-height: 1.5;
    white-space: pre-wrap;
    overflow-wrap: anywhere;
  }
  .mp-custom-context {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .mp-empty-note {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }

  .mp-btn-ghost {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
    letter-spacing: 0.02em;
    background: transparent;
    color: var(--color-primary);
  }
  .mp-btn-ghost:hover {
    background: var(--color-action-hover);
  }
</style>
