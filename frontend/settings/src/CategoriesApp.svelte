<script lang="ts">
  // Categories view (#settings-categories-root). Parity with Categories.razor:
  // add form, active list with drag-reorder (svelte-dnd-action), edit dialog,
  // soft-delete with an "in use" confirm, and a deleted-list with restore.
  import { onMount } from 'svelte';
  import { dndzone, type DndEvent } from 'svelte-dnd-action';
  import type { ShellContext, CategoryDto, CategoryWriteRequest } from './lib/types';
  import { categoriesStore } from './lib/categoriesStore.svelte';
  import { formatDeletedDate } from './lib/dates';
  import { showToast } from './lib/toasts.svelte';
  import CategoryEditDialog from './lib/components/CategoryEditDialog.svelte';
  import ConfirmDialog from './lib/components/ConfirmDialog.svelte';
  import Toasts from './lib/components/Toasts.svelte';

  // ctx is provided by the Razor host; the store calls /api directly (household resolved server-side).
  let { ctx }: { ctx: ShellContext } = $props();

  const store = categoriesStore;

  // One-time load — onMount is not reactive, so it can't form the setup-effect fetch loop.
  onMount(() => {
    void ctx; // host context available; not needed by the store
    void store.load();
  });

  // Add form.
  let newName = $state('');
  let newEmoji = $state('');
  let newColor = $state('#808080');

  async function add() {
    if (!newName.trim()) {
      showToast({ message: 'Category name is required.', kind: 'error' });
      return;
    }
    const body: CategoryWriteRequest = { name: newName.trim(), iconEmoji: newEmoji.trim() || null, color: newColor };
    if (await store.add(body)) {
      newName = '';
      newEmoji = '';
      newColor = '#808080';
    }
  }

  // Drag-reorder: a local copy svelte-dnd-action mutates during a drag, resynced
  // from the store whenever we're NOT dragging (the chores/recipes pattern).
  let rows = $state<CategoryDto[]>([]);
  let dragging = $state(false);
  $effect(() => {
    if (!dragging) rows = [...store.active];
  });
  function handleConsider(e: CustomEvent<DndEvent<CategoryDto>>) {
    dragging = true;
    rows = e.detail.items;
  }
  function handleFinalize(e: CustomEvent<DndEvent<CategoryDto>>) {
    rows = e.detail.items;
    dragging = false;
    void store.reorder(rows.map((c) => c.categoryId));
  }

  // Edit dialog.
  let editing = $state<CategoryDto | null>(null);
  function openEdit(c: CategoryDto) {
    editing = c;
  }
  async function saveEdit(body: CategoryWriteRequest) {
    const target = editing;
    if (!target) return;
    editing = null;
    await store.update(target.categoryId, body);
  }

  // Delete confirm (with the in-use round-trip, parity).
  let confirmCategory = $state<CategoryDto | null>(null);
  let confirmMessage = $state('');
  async function askDelete(c: CategoryDto) {
    const inUse = await store.checkInUse(c.categoryId);
    confirmMessage = inUse
      ? `"${c.name}" is used by ingredients in your recipes. You can still delete it, but those ingredients keep their category.`
      : `Delete the "${c.name}" category?`;
    confirmCategory = c;
  }
  async function confirmDelete() {
    const target = confirmCategory;
    confirmCategory = null;
    if (target) await store.remove(target.categoryId);
  }
</script>

<div class="set-page">
  <h1 class="set-title">Manage Categories</h1>
  <p class="set-subtitle">Customize ingredient categories for your household. Drag to reorder to match your store layout.</p>

  {#if store.loading}
    <div class="set-skeleton">Loading…</div>
  {:else if store.error}
    <div class="set-error">{store.error}</div>
  {:else}
    <!-- Add new -->
    <section class="set-card">
      <h2 class="set-card-title">Add New Category</h2>
      <div class="set-add-row">
        <input class="set-input set-grow" type="text" placeholder="Name" bind:value={newName} onkeydown={(e) => e.key === 'Enter' && add()} />
        <input class="set-input" type="text" placeholder="Emoji" bind:value={newEmoji} />
        <input class="set-color" type="color" bind:value={newColor} aria-label="Color" />
        <button type="button" class="set-btn-primary" onclick={add}>Add</button>
      </div>
    </section>

    <!-- Active list (drag-reorder) -->
    <section class="set-card">
      <h2 class="set-card-title">Active Categories</h2>
      {#if rows.length === 0}
        <div class="set-empty">No categories yet. Add one above.</div>
      {:else}
        <ul
          class="set-list"
          use:dndzone={{ items: rows, flipDurationMs: 150, dropTargetStyle: {}, delayTouchStart: 250 }}
          onconsider={handleConsider}
          onfinalize={handleFinalize}
        >
          {#each rows as cat (cat.categoryId)}
            <li class="set-row">
              <div class="set-row-main">
                <span class="set-grip" aria-hidden="true">⠿</span>
                <span class="set-swatch" style="background-color: {cat.color}"></span>
                <span class="set-name">{cat.name}</span>
                {#if cat.isDefault}
                  <span class="set-chip set-chip-info">Default</span>
                {/if}
              </div>
              <div class="set-row-actions">
                <button type="button" class="set-icon-btn" aria-label="Edit category" onclick={() => openEdit(cat)}>✎</button>
                <button type="button" class="set-icon-btn set-danger" aria-label="Delete category" onclick={() => askDelete(cat)}>🗑</button>
              </div>
            </li>
          {/each}
        </ul>
      {/if}
    </section>

    <!-- Deleted list -->
    {#if store.deleted.length > 0}
      <section class="set-card">
        <h2 class="set-card-title">Deleted Categories</h2>
        {#each store.deleted as cat (cat.categoryId)}
          <div class="set-row set-row-muted">
            <div class="set-row-main">
              <span class="set-swatch" style="background-color: {cat.color}"></span>
              <span class="set-name">{cat.name}</span>
              <span class="set-deleted-note">(deleted {formatDeletedDate(cat.deletedAt)})</span>
            </div>
            <button type="button" class="set-btn-text" onclick={() => store.restore(cat.categoryId)}>Restore</button>
          </div>
        {/each}
      </section>
    {/if}
  {/if}
</div>

<CategoryEditDialog open={editing != null} category={editing} onCancel={() => (editing = null)} onSave={saveEdit} />
<ConfirmDialog
  open={confirmCategory != null}
  title="Delete Category"
  message={confirmMessage}
  confirmLabel="Delete"
  onCancel={() => (confirmCategory = null)}
  onConfirm={confirmDelete}
/>
<Toasts />

<style>
  .set-page {
    max-width: 720px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .set-title {
    margin: 0 0 4px;
    font-size: 1.5rem;
    font-weight: 500;
  }
  .set-subtitle {
    margin: 0 0 20px;
    color: var(--color-text-muted);
    font-size: 0.875rem;
  }
  .set-card {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 16px;
    margin-bottom: 16px;
  }
  .set-card-title {
    margin: 0 0 12px;
    font-size: 1rem;
    font-weight: 500;
  }
  .set-add-row {
    display: flex;
    gap: 8px;
    flex-wrap: wrap;
    align-items: center;
  }
  .set-input {
    font: inherit;
    padding: 10px 12px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--color-line-strong);
    background: var(--color-surface);
    color: var(--color-text);
    min-width: 120px;
  }
  .set-input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
  }
  .set-grow {
    flex: 1;
    min-width: 160px;
  }
  .set-color {
    width: 48px;
    height: 42px;
    padding: 2px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    cursor: pointer;
  }
  .set-btn-primary {
    font: inherit;
    font-weight: 500;
    padding: 10px 20px;
    min-height: 42px;
    border: none;
    border-radius: var(--radius-sm);
    background: var(--color-primary);
    color: #fff;
    cursor: pointer;
  }
  .set-btn-primary:hover {
    background: var(--color-primary-hover);
  }
  .set-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-height: 48px;
  }
  .set-row {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 10px 12px;
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm);
    box-shadow: var(--shadow-1);
    cursor: grab;
  }
  .set-row:active {
    cursor: grabbing;
  }
  .set-row-muted {
    opacity: 0.65;
    cursor: default;
    box-shadow: none;
  }
  .set-row-main {
    display: flex;
    align-items: center;
    gap: 10px;
    min-width: 0;
  }
  .set-grip {
    color: var(--color-text-disabled);
  }
  .set-swatch {
    width: 22px;
    height: 22px;
    border-radius: 4px;
    flex-shrink: 0;
    border: 1px solid var(--color-line);
  }
  .set-name {
    overflow-wrap: anywhere;
  }
  .set-deleted-note {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .set-chip {
    font-size: 0.6875rem;
    font-weight: 500;
    padding: 2px 10px;
    border-radius: 12px;
    white-space: nowrap;
  }
  .set-chip-info {
    border: 1px solid var(--color-info);
    color: var(--color-info);
  }
  .set-row-actions {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-shrink: 0;
  }
  .set-icon-btn {
    width: 34px;
    height: 34px;
    display: grid;
    place-items: center;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.95rem;
  }
  .set-icon-btn:hover {
    background: var(--color-action-hover);
  }
  .set-danger:hover {
    color: var(--color-error);
  }
  .set-btn-text {
    font: inherit;
    font-weight: 500;
    background: transparent;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    padding: 6px 10px;
    border-radius: var(--radius-sm);
  }
  .set-btn-text:hover {
    background: var(--color-action-hover);
  }
  .set-empty,
  .set-skeleton,
  .set-error {
    padding: 16px;
    border-radius: var(--radius-sm);
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .set-error {
    background: rgba(229, 57, 53, 0.08);
    border-left: 4px solid var(--color-error);
    color: var(--color-text);
  }
</style>
