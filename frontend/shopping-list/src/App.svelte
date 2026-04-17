<script lang="ts">
  import type {
    ShellContext,
    ShoppingListDto,
    ShoppingListItemDto,
    ShoppingListSummaryDto,
  } from './lib/types';
  import {
    getList,
    listLists,
    patchItem,
    addItem,
    deleteItem,
    toggleFavorite,
    archiveList,
    renameList,
    clearChecked,
    createList,
    generateFromMealPlan,
    updateSortOrders,
    ApiError,
    type SortOrderUpdate,
  } from './lib/api';
  import CategorySection from './lib/components/CategorySection.svelte';
  import HeaderBar, { type MenuAction } from './lib/components/HeaderBar.svelte';
  import ItemDialog, { type ItemFormValue } from './lib/components/ItemDialog.svelte';
  import PromptDialog from './lib/components/PromptDialog.svelte';
  import ConfirmDialog from './lib/components/ConfirmDialog.svelte';
  import GenerateDialog from './lib/components/GenerateDialog.svelte';
  import Toasts from './lib/components/Toasts.svelte';
  import { showToast } from './lib/toasts.svelte';

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  let currentListId = $state<number | null>(ctx.listId);
  let lists = $state<ShoppingListSummaryDto[]>([]);
  let list = $state<ShoppingListDto | null>(null);
  let loading = $state(true);
  let error = $state<string | null>(null);
  let pendingIds = $state(new Set<number>());

  // Dialogs
  let itemDialogOpen = $state(false);
  let itemDialogMode = $state<'add' | 'edit'>('add');
  let itemDialogInitial = $state<ShoppingListItemDto | null>(null);
  let itemDialogSubmitting = $state(false);

  let confirmDeleteOpen = $state(false);
  let pendingDelete = $state<ShoppingListItemDto | null>(null);
  let confirmClearOpen = $state(false);
  let confirmArchiveOpen = $state(false);

  let renameOpen = $state(false);
  let renameSubmitting = $state(false);
  let newListOpen = $state(false);
  let newListSubmitting = $state(false);
  let generateOpen = $state(false);
  let generateSubmitting = $state(false);

  // Drag reorder enabled on all devices. svelte-dnd-action handles touch via
  // long-press (~500ms) before a drag engages, so scrolling and tap-to-check
  // still work normally. A visible drag handle provides the affordance.
  const dragEnabled = true;

  const CATEGORY_ORDER = [
    'Produce', 'Bakery', 'Meat', 'Dairy', 'Frozen', 'Pantry', 'Spices', 'Beverages', 'Other',
  ];

  let groupedCategories = $derived.by(() => {
    if (!list) return [];
    const groups = new Map<string, ShoppingListItemDto[]>();
    for (const item of list.items) {
      const key = item.category || 'Other';
      if (!groups.has(key)) groups.set(key, []);
      groups.get(key)!.push(item);
    }
    for (const items of groups.values()) {
      items.sort((a, b) => {
        if (a.isChecked !== b.isChecked) return a.isChecked ? 1 : -1;
        return a.sortOrder - b.sortOrder;
      });
    }
    return [...groups.entries()].sort(([a], [b]) => {
      const ai = CATEGORY_ORDER.indexOf(a);
      const bi = CATEGORY_ORDER.indexOf(b);
      const ao = ai === -1 ? 99 : ai;
      const bo = bi === -1 ? 99 : bi;
      if (ao !== bo) return ao - bo;
      return a.localeCompare(b);
    });
  });

  async function loadLists() {
    try {
      lists = await listLists();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }

  async function loadList(listId: number | null) {
    if (listId == null) {
      error = 'No shopping list selected.';
      loading = false;
      return;
    }
    try {
      loading = true;
      error = null;
      list = await getList(listId);
    } catch (e) {
      if (e instanceof ApiError && e.status === 404) {
        await loadLists();
        const fallback = lists[0]?.id ?? null;
        if (fallback != null && fallback !== listId) {
          currentListId = fallback;
          syncUrl(fallback);
          await loadList(fallback);
          return;
        }
        list = null;
        error = 'Shopping list not found.';
      } else {
        error = e instanceof Error ? e.message : String(e);
      }
    } finally {
      loading = false;
    }
  }

  function syncUrl(listId: number) {
    try {
      const path = `/shopping-list/${listId}`;
      if (location.pathname !== path) {
        history.replaceState(history.state, '', path);
      }
    } catch { /* ignore */ }
  }

  function markPending(id: number, on: boolean) {
    const next = new Set(pendingIds);
    if (on) next.add(id);
    else next.delete(id);
    pendingIds = next;
  }

  async function setChecked(item: ShoppingListItemDto, target: boolean) {
    if (!list || currentListId == null) return;
    const prev = { isChecked: item.isChecked, checkedAt: item.checkedAt };
    item.isChecked = target;
    item.checkedAt = target ? new Date().toISOString() : null;
    markPending(item.id, true);
    try {
      const updated = await patchItem(currentListId, item.id, { isChecked: target });
      Object.assign(item, updated);
    } catch (e) {
      Object.assign(item, prev);
      if (e instanceof ApiError && (e.status === 404 || e.status === 409)) {
        await loadList(currentListId);
      } else {
        error = e instanceof Error ? e.message : String(e);
      }
      throw e;
    } finally {
      markPending(item.id, false);
    }
  }

  async function handleToggle(item: ShoppingListItemDto) {
    const target = !item.isChecked;
    try {
      await setChecked(item, target);
      // Only show undo when CHECKING (matches production).
      if (target) {
        const snapshot = item;
        showToast({
          message: `Checked ${snapshot.name}`,
          kind: 'success',
          actionLabel: 'Undo',
          durationMs: 4000,
          onAction: () => {
            setChecked(snapshot, false).catch(() => { /* handled in setChecked */ });
          },
        });
      }
    } catch {
      // already surfaced via error/toast
    }
  }

  async function handleQuantity(item: ShoppingListItemDto, delta: number) {
    if (!list || currentListId == null || item.quantity == null) return;
    const next = item.quantity + delta;
    if (next < 1) return;
    const prev = item.quantity;
    item.quantity = next;
    markPending(item.id, true);
    try {
      const updated = await patchItem(currentListId, item.id, { quantity: next });
      Object.assign(item, updated);
    } catch (e) {
      item.quantity = prev;
      if (e instanceof ApiError && (e.status === 404 || e.status === 409)) {
        await loadList(currentListId);
      } else {
        error = e instanceof Error ? e.message : String(e);
      }
    } finally {
      markPending(item.id, false);
    }
  }

  function openAdd() {
    itemDialogMode = 'add';
    itemDialogInitial = null;
    itemDialogOpen = true;
  }

  function openEdit(item: ShoppingListItemDto) {
    itemDialogMode = 'edit';
    itemDialogInitial = item;
    itemDialogOpen = true;
  }

  async function handleItemDialogSubmit(value: ItemFormValue) {
    if (currentListId == null || !list) return;
    itemDialogSubmitting = true;
    try {
      if (itemDialogMode === 'add') {
        const created = await addItem(currentListId, value);
        list.items = [...list.items, created];
      } else if (itemDialogInitial) {
        const updated = await patchItem(currentListId, itemDialogInitial.id, {
          name: value.name,
          quantity: value.quantity,
          unit: value.unit,
          category: value.category,
        });
        const idx = list.items.findIndex((i) => i.id === updated.id);
        if (idx >= 0) list.items[idx] = updated;
      }
      itemDialogOpen = false;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      itemDialogSubmitting = false;
    }
  }

  function askDelete(item: ShoppingListItemDto) {
    pendingDelete = item;
    confirmDeleteOpen = true;
  }

  async function handleConfirmDelete() {
    if (!list || currentListId == null || !pendingDelete) return;
    const item = pendingDelete;
    confirmDeleteOpen = false;
    markPending(item.id, true);
    try {
      await deleteItem(currentListId, item.id);
      list.items = list.items.filter((i) => i.id !== item.id);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      markPending(item.id, false);
      pendingDelete = null;
    }
  }

  async function handleToggleFavorite() {
    if (currentListId == null || !list) return;
    const prev = list.isFavorite;
    list.isFavorite = !prev;
    try {
      const r = await toggleFavorite(currentListId);
      list.isFavorite = r.isFavorite;
      await loadLists();
    } catch (e) {
      list.isFavorite = prev;
      error = e instanceof Error ? e.message : String(e);
    }
  }

  function handleMenuAction(action: MenuAction) {
    switch (action) {
      case 'new': newListOpen = true; break;
      case 'rename': renameOpen = true; break;
      case 'generate': generateOpen = true; break;
      case 'clear-checked': confirmClearOpen = true; break;
      case 'archive': confirmArchiveOpen = true; break;
    }
  }

  async function handleRename(newName: string) {
    if (currentListId == null || !list) return;
    renameSubmitting = true;
    try {
      const r = await renameList(currentListId, newName);
      list.name = r.name;
      await loadLists();
      renameOpen = false;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      renameSubmitting = false;
    }
  }

  async function handleCreateList(name: string) {
    newListSubmitting = true;
    try {
      const created = await createList(name);
      await loadLists();
      currentListId = created.id;
      syncUrl(created.id);
      await loadList(created.id);
      newListOpen = false;
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      newListSubmitting = false;
    }
  }

  async function handleGenerate(body: { startDate: string; endDate: string }) {
    generateSubmitting = true;
    try {
      const created = await generateFromMealPlan(body);
      await loadLists();
      currentListId = created.id;
      syncUrl(created.id);
      await loadList(created.id);
      generateOpen = false;
      showToast({
        message: `Generated with ${created.itemCount} items`,
        kind: 'success',
        durationMs: 3000,
      });
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      generateSubmitting = false;
    }
  }

  async function handleConfirmClearChecked() {
    if (currentListId == null) return;
    confirmClearOpen = false;
    try {
      const { removed } = await clearChecked(currentListId);
      await loadList(currentListId);
      showToast({ message: `Cleared ${removed} checked items`, kind: 'success' });
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }

  async function handleConfirmArchive() {
    if (currentListId == null) return;
    confirmArchiveOpen = false;
    try {
      await archiveList(currentListId);
      await loadLists();
      const next = lists.find((l) => l.id !== currentListId)?.id ?? null;
      currentListId = next;
      if (next != null) {
        syncUrl(next);
        await loadList(next);
      } else {
        list = null;
      }
      showToast({ message: 'List archived', kind: 'info' });
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }

  async function handleSelectList(id: number) {
    currentListId = id;
    syncUrl(id);
    await loadList(id);
  }

  // ── Drag-and-drop reorder ───────────────────────────────────────────────
  //
  // svelte-dnd-action emits `consider` (live during drag) and `finalize`
  // (drop). We optimistically splice the reordered items back into
  // `list.items` with updated sortOrder + category on each event so the
  // categorized view stays coherent while dragging. On finalize we POST
  // the new orderings to the server.

  function applyCategoryItems(category: string, reordered: ShoppingListItemDto[]) {
    if (!list) return;
    // Give every dragged item the new category + a fresh sortOrder.
    for (let i = 0; i < reordered.length; i++) {
      reordered[i].category = category;
      reordered[i].sortOrder = i;
    }
    // Rebuild list.items: keep items not in the destination category, then
    // append the reordered block for this category.
    const kept = list.items.filter(
      (i) => i.category !== category && !reordered.find((r) => r.id === i.id),
    );
    list.items = [...kept, ...reordered];
  }

  async function handleDnd(
    category: string,
    reordered: ShoppingListItemDto[],
    phase: 'consider' | 'finalize',
  ) {
    applyCategoryItems(category, reordered);
    if (phase !== 'finalize' || currentListId == null) return;
    const updates: SortOrderUpdate[] = reordered.map((item, index) => ({
      itemId: item.id,
      sortOrder: index,
      category,
    }));
    try {
      await updateSortOrders(currentListId, updates);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
      await loadList(currentListId);
    }
  }

  function handleVisibility() {
    if (document.visibilityState === 'visible' && currentListId != null) {
      loadList(currentListId);
    }
  }

  $effect(() => {
    (async () => {
      await loadLists();
      if (currentListId == null && lists.length > 0) {
        currentListId = lists[0].id;
        syncUrl(currentListId);
      }
      await loadList(currentListId);
    })();
    document.addEventListener('visibilitychange', handleVisibility);
    return () => document.removeEventListener('visibilitychange', handleVisibility);
  });
</script>

<div class="sl-container">
  <HeaderBar
    {lists}
    {currentListId}
    currentListName={list?.name ?? null}
    isFavorite={list?.isFavorite ?? false}
    onSelect={handleSelectList}
    onToggleFavorite={handleToggleFavorite}
    onMenuAction={handleMenuAction}
  />

  {#if error}
    <div class="sl-inline-error" role="alert">
      <span>{error}</span>
      <button
        type="button"
        class="sl-inline-retry"
        onclick={() => currentListId != null && loadList(currentListId)}
      >
        Retry
      </button>
    </div>
  {/if}

  {#if loading && !list}
    <div class="sl-loading">Loading…</div>
  {:else if list}
    {#if list.items.length === 0}
      <div class="sl-empty">
        <p>No items in shopping list.</p>
        <button type="button" class="sl-btn-primary" onclick={openAdd}>Add the first item</button>
      </div>
    {:else}
      {#each groupedCategories as [category, items] (category)}
        <CategorySection
          {category}
          {items}
          {pendingIds}
          {dragEnabled}
          onToggle={handleToggle}
          onQuantity={handleQuantity}
          onEdit={openEdit}
          onDelete={askDelete}
          onDnd={handleDnd}
        />
      {/each}
    {/if}
  {:else if !loading}
    <div class="sl-empty">
      <p>No active shopping lists.</p>
      <button type="button" class="sl-btn-primary" onclick={() => (newListOpen = true)}>
        Create a list
      </button>
    </div>
  {/if}
</div>

<button
  type="button"
  class="sl-fab"
  aria-label="Add item"
  onclick={openAdd}
  disabled={!list || currentListId == null}
>
  <svg viewBox="0 0 24 24" width="24" height="24">
    <path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" fill="currentColor" />
  </svg>
</button>

<ItemDialog
  open={itemDialogOpen}
  mode={itemDialogMode}
  submitting={itemDialogSubmitting}
  initial={itemDialogInitial}
  onClose={() => (itemDialogOpen = false)}
  onSubmit={handleItemDialogSubmit}
/>

<ConfirmDialog
  open={confirmDeleteOpen}
  title="Delete Item"
  message={pendingDelete ? `Delete ${pendingDelete.name} from the shopping list?` : ''}
  confirmLabel="Delete"
  danger
  onCancel={() => { confirmDeleteOpen = false; pendingDelete = null; }}
  onConfirm={handleConfirmDelete}
/>

<ConfirmDialog
  open={confirmClearOpen}
  title="Clear Checked Items"
  message="Remove all checked items from this shopping list?"
  confirmLabel="Clear"
  onCancel={() => (confirmClearOpen = false)}
  onConfirm={handleConfirmClearChecked}
/>

<ConfirmDialog
  open={confirmArchiveOpen}
  title="Archive List"
  message={list ? `Archive ${list.name}?` : ''}
  confirmLabel="Archive"
  onCancel={() => (confirmArchiveOpen = false)}
  onConfirm={handleConfirmArchive}
/>

<PromptDialog
  open={renameOpen}
  title="Rename List"
  label="Name"
  initial={list?.name ?? ''}
  confirmLabel="Save"
  submitting={renameSubmitting}
  onClose={() => (renameOpen = false)}
  onSubmit={handleRename}
/>

<PromptDialog
  open={newListOpen}
  title="Create New List"
  label="Name"
  initial="Shopping List"
  confirmLabel="Create"
  submitting={newListSubmitting}
  onClose={() => (newListOpen = false)}
  onSubmit={handleCreateList}
/>

<GenerateDialog
  open={generateOpen}
  submitting={generateSubmitting}
  onClose={() => (generateOpen = false)}
  onSubmit={handleGenerate}
/>

<Toasts />

<style>
  .sl-container {
    max-width: 960px;
    margin: 0 auto;
    padding: 24px 16px 96px;
  }
  .sl-loading,
  .sl-empty {
    padding: 48px 16px;
    text-align: center;
    color: var(--color-text-muted);
  }
  .sl-empty {
    display: flex;
    flex-direction: column;
    align-items: center;
    gap: 16px;
  }
  .sl-inline-error {
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
  .sl-inline-retry,
  .sl-btn-primary {
    font: inherit;
    border: none;
    cursor: pointer;
    border-radius: var(--radius-sm);
  }
  .sl-inline-retry {
    padding: 4px 12px;
    background: transparent;
    color: var(--color-error);
    font-weight: 500;
  }
  .sl-inline-retry:hover {
    background: rgba(229, 57, 53, 0.12);
  }
  .sl-btn-primary {
    padding: 10px 20px;
    background: var(--color-primary);
    color: #fff;
    font-weight: 500;
    box-shadow: var(--shadow-1);
  }
  .sl-btn-primary:hover {
    background: var(--color-primary-hover);
  }

  .sl-fab {
    position: fixed;
    bottom: 24px;
    right: 24px;
    width: 56px;
    height: 56px;
    border-radius: 50%;
    border: none;
    background: var(--color-primary);
    color: #fff;
    display: grid;
    place-items: center;
    cursor: pointer;
    box-shadow: var(--shadow-4);
    transition:
      background-color 0.15s,
      transform 0.1s;
    z-index: 10;
  }
  .sl-fab:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .sl-fab:active:not(:disabled) {
    transform: scale(0.95);
  }
  .sl-fab:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
