<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // Root of the recipes EDIT view (#recipes-edit-root, data-view="edit").
  // ctx.recipeId is null for /recipes/new, set for /recipes/edit/{id}. Loads the
  // recipe (or draft, or blank) into the edit store and renders the form
  // sections, ingredient entry/list (dnd), instructions, autosave indicator, and
  // Save/Cancel/Delete. Single-user form ⇒ NO liveness.
  //
  // ⚠ Loop safety (spec §10): the one-time setup $effect is wrapped ENTIRELY in
  // untrack() — store.load() reads then writes store $state, so without untrack
  // the effect would loop. untrack gives it zero reactive deps (runs once).
  //
  // Nav-lock: a `beforeunload` handler warns on unsaved changes (replaces the
  // Blazor NavigationLock). Save/Cancel/Delete clear `dirty` before navigating,
  // so only an unsaved manual nav-away prompts.
  // ───────────────────────────────────────────────────────────────────────
  import { untrack } from 'svelte';
  import type { ShellContext } from './lib/types';
  import { recipeEditStore } from './lib/recipeEditStore.svelte';
  import BasicInfoSection from './lib/components/BasicInfoSection.svelte';
  import ImageSection from './lib/components/ImageSection.svelte';
  import IngredientEntry from './lib/components/IngredientEntry.svelte';
  import IngredientList from './lib/components/IngredientList.svelte';
  import BulkPasteDialog from './lib/components/BulkPasteDialog.svelte';
  import ImagePickerDialog from './lib/components/ImagePickerDialog.svelte';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  const store = recipeEditStore;

  let bulkOpen = $state(false);
  let pickerOpen = $state(false);
  let deleteOpen = $state(false);

  function handleBeforeUnload(e: BeforeUnloadEvent): void {
    if (store.dirty) {
      e.preventDefault();
      e.returnValue = '';
    }
  }

  $effect(() => {
    untrack(() => {
      store.init(ctx);
      store.load(ctx.recipeId); // reads then writes store $state — MUST be inside untrack
      window.addEventListener('beforeunload', handleBeforeUnload);
    });

    return () => {
      window.removeEventListener('beforeunload', handleBeforeUnload);
      store.teardown();
    };
  });
</script>

<div class="rc-container">
  <header class="rc-header">
    <h1 class="rc-title">{store.isEdit ? 'Edit Recipe' : 'New Recipe'}</h1>
    {#if store.isEdit}
      <button type="button" class="rc-btn-danger-outline" onclick={() => (deleteOpen = true)}>Delete</button>
    {/if}
  </header>

  {#if store.loading}
    <div class="rc-loading-bar"><span></span></div>
  {:else}
    {#if store.conflict}
      <!-- Stale-version 409: NON-destructive — the form (and the user's typing) stays; Reload refetches
           the latest (typed state is preserved as the draft first, see reloadAfterConflict). -->
      <div class="rc-conflict-banner" role="alert">
        <span class="rc-conflict-text">
          This recipe changed — reload to get the latest. Your edits stay on screen until you reload (reloading
          keeps them as a draft).
        </span>
        <button
          type="button"
          class="rc-btn-conflict"
          onclick={() => store.reloadAfterConflict()}
          disabled={store.reloading}
        >
          {store.reloading ? 'Reloading…' : 'Reload'}
        </button>
      </div>
    {/if}

    {#if store.error}
      <div class="rc-inline-error" role="alert">{store.error}</div>
    {/if}

    <BasicInfoSection />

    <ImageSection onChooseExisting={() => (pickerOpen = true)} />

    <section class="rc-paper">
      <h2 class="rc-section-title">Ingredients</h2>
      <IngredientEntry
        categories={store.categories}
        onAdd={(i) => store.addIngredient(i)}
        onBulkOpen={() => (bulkOpen = true)}
      />
      <IngredientList
        ingredients={store.ingredients}
        onReorder={(rows) => store.reorder(rows)}
        onEdit={(id) => store.editIngredient(id)}
        onRemove={(id) => store.removeIngredient(id)}
      />
    </section>

    <section class="rc-paper">
      <h2 class="rc-section-title">Instructions</h2>
      <textarea
        class="rc-input rc-instructions"
        rows="10"
        bind:value={store.form.instructions}
        oninput={() => store.onEdit()}
        placeholder="Supports Markdown formatting"
      ></textarea>
    </section>

    <div class="rc-footer">
      <div class="rc-autosave" aria-live="polite">
        {#if store.autosaveStatus === 'saving'}
          <span class="rc-spinner" aria-hidden="true"></span>
          <span class="rc-autosave-text">Saving draft…</span>
        {:else if store.autosaveStatus === 'saved'}
          <span class="rc-autosave-saved">✓ Draft saved</span>
        {/if}
      </div>

      <div class="rc-footer-actions">
        <button type="button" class="rc-btn-ghost" onclick={() => store.cancel()}>Cancel</button>
        <button type="button" class="rc-btn-solid" onclick={() => store.save()} disabled={store.saving}>
          {store.saving ? 'Saving…' : store.isEdit ? 'Save Changes' : 'Create Recipe'}
        </button>
      </div>
    </div>
  {/if}
</div>

<BulkPasteDialog
  open={bulkOpen}
  categories={store.categories}
  onClose={() => (bulkOpen = false)}
  onImport={(rows) => store.addBulk(rows)}
/>

<ImagePickerDialog
  open={pickerOpen}
  currentImage={store.form.imagePath}
  onClose={() => (pickerOpen = false)}
  onSelect={(path) => store.setImage(path)}
/>

<ConfirmDialog
  open={deleteOpen}
  danger
  title="Delete Recipe"
  message={`Are you sure you want to delete "${store.form.name}"? This action cannot be undone.`}
  onCancel={() => (deleteOpen = false)}
  onConfirm={() => {
    deleteOpen = false;
    store.delete();
  }}
/>

<style>
  .rc-container {
    max-width: 880px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .rc-header {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    margin-bottom: 16px;
  }
  .rc-title {
    margin: 0;
    font-size: 2.125rem;
    font-weight: 400;
    color: var(--color-text);
  }
  .rc-paper {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 16px;
    margin-bottom: 16px;
  }
  .rc-section-title {
    margin: 0 0 12px;
    font-size: 1.125rem;
    font-weight: 500;
  }
  .rc-input {
    font: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    color: var(--color-text);
    width: 100%;
  }
  .rc-input:focus {
    outline: none;
    border-color: var(--color-primary);
  }
  .rc-instructions {
    resize: vertical;
    min-height: 200px;
  }
  .rc-loading-bar {
    height: 4px;
    background: var(--color-action-hover);
    border-radius: 2px;
    overflow: hidden;
    margin: 24px 0;
  }
  .rc-loading-bar span {
    display: block;
    height: 100%;
    width: 40%;
    background: var(--color-primary);
    animation: rc-indeterminate 1.2s ease-in-out infinite;
  }
  @keyframes rc-indeterminate {
    0% {
      transform: translateX(-100%);
    }
    100% {
      transform: translateX(350%);
    }
  }
  .rc-inline-error {
    padding: 10px 16px;
    margin-bottom: 16px;
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    border-radius: var(--radius-sm);
    color: var(--color-error);
    font-size: 0.875rem;
  }
  .rc-conflict-banner {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
    padding: 10px 16px;
    margin-bottom: 16px;
    background: rgba(251, 140, 0, 0.1);
    border-left: 4px solid var(--color-warning);
    border-radius: var(--radius-sm);
    color: var(--color-text);
    font-size: 0.875rem;
  }
  .rc-conflict-text {
    flex: 1 1 240px;
  }
  .rc-btn-conflict {
    font: inherit;
    font-weight: 500;
    padding: 8px 16px;
    border: 1px solid var(--color-warning);
    border-radius: var(--radius-sm);
    background: transparent;
    color: var(--color-text);
    cursor: pointer;
    min-height: 36px;
  }
  .rc-btn-conflict:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .rc-btn-conflict:disabled {
    opacity: 0.6;
    cursor: default;
  }
  .rc-footer {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    flex-wrap: wrap;
  }
  .rc-autosave {
    display: flex;
    align-items: center;
    gap: 8px;
    min-height: 24px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .rc-autosave-saved {
    color: var(--color-success);
  }
  .rc-spinner {
    width: 14px;
    height: 14px;
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
  .rc-footer-actions {
    display: flex;
    gap: 8px;
    margin-left: auto;
  }
  .rc-btn-ghost,
  .rc-btn-solid,
  .rc-btn-danger-outline {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
  }
  .rc-btn-ghost {
    border: none;
    background: transparent;
    color: var(--color-text-muted);
  }
  .rc-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .rc-btn-solid {
    border: none;
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-solid:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .rc-btn-solid:disabled {
    opacity: 0.6;
    cursor: default;
  }
  .rc-btn-danger-outline {
    border: 1px solid var(--color-error);
    background: transparent;
    color: var(--color-error);
  }
  .rc-btn-danger-outline:hover {
    background: rgba(229, 57, 53, 0.08);
  }
</style>
