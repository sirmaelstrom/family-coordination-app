<script lang="ts">
  // Import-from-URL dialog (mirrors ImportRecipeDialog.razor): URL field, a
  // supported-sites note, a spinner (YouTube variant warns it's slower), duplicate
  // detection ("View Existing" / "Import Anyway"=force), and an error → "Add
  // Manually" fallback. On success the parent navigates to /recipes/edit/{id}.
  import { importRecipe, ApiError } from '../api';

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

  let isYouTube = $derived(/youtube\.com|youtu\.be/i.test(url));

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
  }

  function handleClose(): void {
    reset();
    onClose();
  }

  async function doImport(force = false): Promise<void> {
    if (!url.trim() || importing) return;
    importing = true;
    error = null;
    showManual = false;
    existingId = null;
    try {
      const res = await importRecipe(url.trim(), force);
      if (res.success && res.recipeId != null) {
        onImported(res.recipeId);
        return;
      }
      if (res.existingRecipeId != null) {
        existingId = res.existingRecipeId;
        error = res.errorMessage ?? 'This recipe has already been imported.';
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

  function viewExisting(): void {
    if (existingId != null) window.location.assign(`/recipes/edit/${existingId}`);
  }

  function importAnyway(): void {
    void doImport(true);
  }

  function addManually(): void {
    window.location.assign('/recipes/new');
  }
</script>

<dialog bind:this={dialogEl} onclose={handleClose} class="rc-dialog">
  {#if open}
    <header class="rc-dialog-head">
      <h2>Import Recipe</h2>
      <button type="button" class="rc-dialog-x" aria-label="Close" onclick={handleClose}>×</button>
    </header>

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
              : 'Importing recipe…'}
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
        onclick={() => doImport(false)}
        disabled={importing || !url.trim()}
      >
        Import
      </button>
    </footer>
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
