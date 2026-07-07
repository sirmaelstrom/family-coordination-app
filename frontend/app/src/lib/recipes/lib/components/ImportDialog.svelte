<script lang="ts">
  // Import-from-URL dialog — scrape → PREVIEW → confirm (nothing is persisted until
  // the user confirms). Phase 'url': URL field, supported-sites note, a spinner
  // (YouTube variant warns it's slower), duplicate detection ("View Existing" /
  // "Import Anyway"=force), and an error → "Add Manually" fallback. Phase 'preview':
  // the parsed result (title, image, meta, ingredients, steps, source) — a PARTIAL
  // parse still previews (with a warning; confirm needs at least a title). Confirm
  // POSTs the payload to the plain create endpoint, then the parent navigates to
  // /recipes/edit/{id}; Back/Cancel persist nothing.
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import { previewImport, createRecipe, ApiError } from '../api';
  import type { PartialRecipeDataDto, RecipePreviewDto, RecipeWriteRequest } from '../types';
  import { formatExactQuantity } from '../quantity';

  interface Props {
    open: boolean;
    onClose: () => void;
    onImported: (recipeId: number) => void;
  }

  let { open, onClose, onImported }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);
  let url = $state('');
  let importing = $state(false);
  let error = $state<string | null>(null);
  let existingId = $state<number | null>(null);
  let showManual = $state(false);

  // Preview phase state. `preview` = full parse; `partial` = degraded parse (still shown).
  let phase = $state<'url' | 'preview'>('url');
  let preview = $state<RecipePreviewDto | null>(null);
  let partial = $state<PartialRecipeDataDto | null>(null);
  let previewWarning = $state<string | null>(null);
  let confirming = $state(false);
  let confirmError = $state<string | null>(null);

  let isYouTube = $derived(/youtube\.com|youtu\.be/i.test(url));

  // One render model for both full and partial previews.
  let view = $derived.by(() => {
    if (preview) {
      return {
        name: preview.name as string | null,
        description: preview.description,
        instructions: preview.instructions,
        imageUrl: preview.imagePath,
        sourceUrl: preview.sourceUrl ?? url.trim(),
        servings: preview.servings,
        prepTimeMinutes: preview.prepTimeMinutes,
        cookTimeMinutes: preview.cookTimeMinutes,
        ingredients: preview.ingredients.map((i) => {
          const qty = i.quantity != null ? formatExactQuantity(i.quantity) : null;
          const line = [qty, i.unit, i.name].filter(Boolean).join(' ');
          return i.notes ? `${line} (${i.notes})` : line;
        }),
      };
    }
    if (partial) {
      return {
        name: partial.name,
        description: partial.description,
        instructions: partial.instructions,
        imageUrl: partial.imageUrl,
        sourceUrl: url.trim(),
        servings: partial.servings,
        prepTimeMinutes: partial.prepTimeMinutes,
        cookTimeMinutes: partial.cookTimeMinutes,
        ingredients: partial.ingredientStrings ?? [],
      };
    }
    return null;
  });

  let canConfirm = $derived(!confirming && (preview != null || !!partial?.name?.trim()));

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  function reset(): void {
    url = '';
    importing = false;
    error = null;
    existingId = null;
    showManual = false;
    phase = 'url';
    preview = null;
    partial = null;
    previewWarning = null;
    confirming = false;
    confirmError = null;
  }

  function handleClose(): void {
    reset();
    onClose();
  }

  /** Back to the URL step — discards the preview (nothing was persisted). */
  function backToUrl(): void {
    phase = 'url';
    preview = null;
    partial = null;
    previewWarning = null;
    confirmError = null;
    error = null;
  }

  async function doPreview(force = false): Promise<void> {
    if (!url.trim() || importing) return;
    importing = true;
    error = null;
    showManual = false;
    existingId = null;
    try {
      const res = await previewImport(url.trim(), force);
      if (res.success && res.recipe != null) {
        preview = res.recipe;
        partial = null;
        previewWarning = null;
        phase = 'preview';
        return;
      }
      if (res.existingRecipeId != null) {
        existingId = res.existingRecipeId;
        error = res.errorMessage ?? 'This recipe has already been imported.';
        return;
      }
      if (res.partialData != null) {
        // Failure honesty: show what DID come back before the user decides.
        preview = null;
        partial = res.partialData;
        previewWarning = res.errorMessage ?? 'Only part of the recipe could be extracted.';
        phase = 'preview';
        return;
      }
      error = res.errorMessage ?? 'Import failed for unknown reason.';
      // Offer manual entry unless the URL itself was invalid.
      showManual = res.errorType !== 'InvalidUrl';
    } catch (e) {
      error =
        e instanceof ApiError
          ? `Import failed (HTTP ${e.status}).`
          : 'An error occurred during import.';
      showManual = true;
    } finally {
      importing = false;
    }
  }

  /** Confirm: create the previewed recipe via the plain create endpoint, then navigate. */
  async function confirmImport(): Promise<void> {
    if (!canConfirm) return;
    // version: null — this is a CREATE; the xmin concurrency token only applies to edits (PUT).
    const body: RecipeWriteRequest | null = preview
      ? { ...preview, version: null }
      : partial?.name?.trim()
        ? {
            version: null,
            name: partial.name.trim(),
            description: partial.description,
            instructions: partial.instructions,
            sourceUrl: url.trim(),
            servings: partial.servings,
            prepTimeMinutes: partial.prepTimeMinutes,
            cookTimeMinutes: partial.cookTimeMinutes,
            recipeType: 'main',
            imagePath: partial.imageUrl,
            ingredients: (partial.ingredientStrings ?? []).map((s, i) => ({
              name: s,
              quantity: null,
              unit: null,
              category: 'Pantry',
              notes: null,
              groupName: null,
              sortOrder: i,
            })),
          }
        : null;
    if (!body) return;

    confirming = true;
    confirmError = null;
    try {
      const created = await createRecipe(body);
      onImported(created.recipeId);
    } catch (e) {
      confirmError =
        e instanceof ApiError
          ? `Could not save the recipe (HTTP ${e.status}).`
          : 'An error occurred while saving the recipe.';
      confirming = false;
    }
  }

  function viewExisting(): void {
    if (existingId != null) void goto(`${base}/recipes/edit/${existingId}`);
  }

  function importAnyway(): void {
    void doPreview(true);
  }

  function addManually(): void {
    void goto(`${base}/recipes/new`);
  }
</script>

<dialog bind:this={dialogEl} onclose={handleClose} class="rc-dialog">
  {#if open}
    <header class="rc-dialog-head">
      <h2>{phase === 'preview' ? 'Preview Recipe' : 'Import Recipe'}</h2>
      <button type="button" class="rc-dialog-x" aria-label="Close" onclick={handleClose}>×</button>
    </header>

    {#if phase === 'url'}
      <div class="rc-dialog-body">
        <p class="rc-import-intro">Paste a recipe URL to import. Tested and working with:</p>
        <p class="rc-import-sites">✓ AllRecipes • BBC Good Food • NYT Cooking • Serious Eats • YouTube</p>

        <label class="rc-field">
          <span class="rc-field-label">Recipe URL</span>
          <input
            type="url"
            class="rc-input"
            placeholder="https://www.allrecipes.com/recipe/..."
            bind:value={url}
            disabled={importing}
          />
        </label>

        {#if importing}
          <div class="rc-import-progress">
            <span class="rc-spinner" aria-hidden="true"></span>
            <span>
              {isYouTube
                ? 'Extracting recipe from video… This may take a moment.'
                : 'Fetching recipe…'}
            </span>
          </div>
        {/if}

        {#if error}
          <div class="rc-import-alert" class:rc-alert-info={existingId != null}>
            <p class="rc-alert-msg">{error}</p>
            {#if existingId != null}
              <div class="rc-alert-actions">
                <button type="button" class="rc-text-btn" onclick={viewExisting}>View Existing</button>
                <button type="button" class="rc-text-btn" onclick={importAnyway}>Import Anyway</button>
              </div>
            {:else if showManual}
              <button type="button" class="rc-text-btn" onclick={addManually}>Add Recipe Manually</button>
            {/if}
          </div>
        {/if}
      </div>

      <footer class="rc-dialog-actions">
        <button type="button" class="rc-btn-ghost" onclick={handleClose} disabled={importing}>Cancel</button>
        <button
          type="button"
          class="rc-btn-solid"
          onclick={() => doPreview(false)}
          disabled={importing || !url.trim()}
        >
          Import
        </button>
      </footer>
    {:else if view}
      <div class="rc-dialog-body">
        {#if previewWarning}
          <div class="rc-import-alert rc-preview-warning">
            <p class="rc-alert-msg">{previewWarning}</p>
            {#if !canConfirm}
              <button type="button" class="rc-text-btn" onclick={addManually}>Add Recipe Manually</button>
            {/if}
          </div>
        {/if}

        <p class="rc-preview-hint">Nothing is saved yet — review what was found, then add it.</p>

        {#if view.imageUrl}
          <img
            class="rc-preview-img"
            src={view.imageUrl}
            alt=""
            loading="lazy"
            onerror={(e) => ((e.currentTarget as HTMLImageElement).style.display = 'none')}
          />
        {/if}

        <h3 class="rc-preview-name" class:rc-preview-missing={!view.name?.trim()}>
          {view.name?.trim() || 'No title found'}
        </h3>

        {#if view.description}
          <p class="rc-preview-desc">{view.description}</p>
        {/if}

        {#if view.servings != null || view.prepTimeMinutes != null || view.cookTimeMinutes != null}
          <p class="rc-preview-meta">
            {#if view.servings != null}<span>Serves {view.servings}</span>{/if}
            {#if view.prepTimeMinutes != null}<span>Prep {view.prepTimeMinutes} min</span>{/if}
            {#if view.cookTimeMinutes != null}<span>Cook {view.cookTimeMinutes} min</span>{/if}
          </p>
        {/if}

        <h4 class="rc-preview-heading">Ingredients ({view.ingredients.length})</h4>
        {#if view.ingredients.length > 0}
          <ul class="rc-preview-ingredients">
            {#each view.ingredients as line, i (i)}
              <li>{line}</li>
            {/each}
          </ul>
        {:else}
          <p class="rc-preview-missing">No ingredients found — you can add them after saving.</p>
        {/if}

        <h4 class="rc-preview-heading">Instructions</h4>
        {#if view.instructions?.trim()}
          <p class="rc-preview-steps">{view.instructions}</p>
        {:else}
          <p class="rc-preview-missing">No instructions found.</p>
        {/if}

        <p class="rc-preview-source">Source: <span class="rc-preview-url">{view.sourceUrl}</span></p>

        {#if confirming}
          <div class="rc-import-progress">
            <span class="rc-spinner" aria-hidden="true"></span>
            <span>Saving recipe…</span>
          </div>
        {/if}

        {#if confirmError}
          <div class="rc-import-alert">
            <p class="rc-alert-msg">{confirmError}</p>
          </div>
        {/if}
      </div>

      <footer class="rc-dialog-actions">
        <button type="button" class="rc-btn-ghost rc-btn-back" onclick={backToUrl} disabled={confirming}>
          Back
        </button>
        <button type="button" class="rc-btn-ghost" onclick={handleClose} disabled={confirming}>Cancel</button>
        <button type="button" class="rc-btn-solid" onclick={confirmImport} disabled={!canConfirm}>
          Add Recipe
        </button>
      </footer>
    {/if}
  {/if}
</dialog>

<style>
  .rc-dialog[open] {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(520px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
    display: flex;
    flex-direction: column;
  }
  .rc-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .rc-dialog-head {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 16px 16px 12px 24px;
    border-bottom: 1px solid var(--color-line);
  }
  .rc-dialog-head h2 {
    margin: 0;
    flex: 1;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .rc-dialog-x {
    flex-shrink: 0;
    width: 36px;
    height: 36px;
    display: grid;
    place-items: center;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    font-size: 1.4rem;
    line-height: 1;
    cursor: pointer;
  }
  .rc-dialog-x:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .rc-dialog-body {
    overflow-y: auto;
    padding: 16px 24px;
    flex: 1;
  }
  .rc-import-intro {
    margin: 0 0 8px;
    font-size: 0.9375rem;
  }
  .rc-import-sites {
    margin: 0 0 16px;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .rc-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .rc-field-label {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .rc-input {
    font: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    color: var(--color-text);
  }
  .rc-input:focus {
    outline: none;
    border-color: var(--color-primary);
  }
  .rc-import-progress {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-top: 16px;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .rc-spinner {
    width: 18px;
    height: 18px;
    border: 2px solid var(--color-line-strong);
    border-top-color: var(--color-primary);
    border-radius: 50%;
    animation: rc-spin 0.8s linear infinite;
  }
  @keyframes rc-spin {
    to {
      transform: rotate(360deg);
    }
  }
  .rc-import-alert {
    margin-top: 16px;
    padding: 12px 16px;
    border-radius: var(--radius-sm);
    border-left: 4px solid var(--color-warning);
    background: rgba(251, 140, 0, 0.08);
  }
  .rc-alert-info {
    border-left-color: var(--color-info);
    background: rgba(30, 136, 229, 0.08);
  }
  .rc-alert-msg {
    margin: 0;
    font-size: 0.875rem;
  }
  .rc-alert-actions {
    display: flex;
    gap: 12px;
    margin-top: 8px;
  }
  .rc-text-btn {
    font: inherit;
    background: transparent;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    font-size: 0.875rem;
    font-weight: 500;
    margin-top: 8px;
    padding: 0;
  }
  .rc-alert-actions .rc-text-btn {
    margin-top: 0;
  }
  .rc-text-btn:hover {
    text-decoration: underline;
  }
  /* ── Preview phase ── */
  .rc-preview-hint {
    margin: 0 0 12px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .rc-preview-warning {
    margin: 0 0 16px; /* it leads the body — cancel rc-import-alert's margin-top */
  }
  .rc-preview-img {
    display: block;
    width: 100%;
    max-height: 220px;
    object-fit: cover;
    border-radius: var(--radius-sm);
    margin-bottom: 12px;
  }
  .rc-preview-name {
    margin: 0 0 4px;
    font-size: 1.125rem;
    font-weight: 600;
  }
  .rc-preview-desc {
    margin: 0 0 8px;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .rc-preview-meta {
    display: flex;
    flex-wrap: wrap;
    gap: 4px 16px;
    margin: 0 0 8px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .rc-preview-heading {
    margin: 16px 0 6px;
    font-size: 0.875rem;
    font-weight: 600;
  }
  .rc-preview-ingredients {
    margin: 0;
    padding-left: 20px;
    font-size: 0.875rem;
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .rc-preview-steps {
    margin: 0;
    font-size: 0.875rem;
    white-space: pre-line;
  }
  .rc-preview-missing {
    margin: 0;
    font-size: 0.875rem;
    font-style: italic;
    color: var(--color-text-muted);
  }
  .rc-preview-source {
    margin: 16px 0 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .rc-preview-url {
    word-break: break-all;
  }
  .rc-btn-back {
    margin-right: auto;
  }

  .rc-dialog-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 12px 24px calc(16px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }
  .rc-btn-ghost,
  .rc-btn-solid {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
  }
  .rc-btn-ghost {
    background: transparent;
    color: var(--color-text-muted);
  }
  .rc-btn-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .rc-btn-solid {
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-solid:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .rc-btn-solid:disabled,
  .rc-btn-ghost:disabled {
    opacity: 0.6;
    cursor: default;
  }
</style>
