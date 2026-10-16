<script lang="ts">
  import { untrack } from 'svelte';
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import type { ShellContext } from '$lib/session.svelte';
  import type { ShoppingListItemDto } from './lib/types';
  import { startLiveness } from '$lib/liveness';
  import { ShoppingListState } from './lib/shoppingListState.svelte';
  import CategorySection from './lib/components/CategorySection.svelte';
  import HeaderBar, { type MenuAction } from './lib/components/HeaderBar.svelte';
  import ItemDialog, { type ItemFormValue } from './lib/components/ItemDialog.svelte';
  import PromptDialog from '$lib/shared/PromptDialog.svelte';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';
  import GenerateDialog from './lib/components/GenerateDialog.svelte';
  import { showToast } from '$lib/shared/toast-store.svelte';

  interface Props {
    ctx: ShellContext;
  }

  let { ctx }: Props = $props();

  function syncUrl(listId: number) {
    try {
      // Base-aware shallow URL sync (D6). A history.replaceState — NOT goto() —
      // so the island keeps its state: goto would remount the route and re-run
      // the session/ctx init. base is '/app', so the URL stays under /app.
      const path = `${base}/shopping-list/${listId}`;
      if (location.pathname !== path) {
        history.replaceState(history.state, '', path);
      }
    } catch { /* ignore */ }
  }

  // The list state and its load/write ordering live in ShoppingListState (quest 41337b6f, spec §5 row 11):
  // one Resource loads the list + the summaries, and every write runs inside its window — no GET that
  // overlaps a write can commit, in either response order. The template reads these through the aliases.
  const sl = new ShoppingListState(ctx.listId, syncUrl);
  const lists = $derived(sl.lists);
  const currentListId = $derived(sl.currentListId);
  const list = $derived(sl.list);
  const loading = $derived(sl.loading);
  const error = $derived(sl.error);
  const pendingIds = $derived(sl.pendingIds);

  // Dialogs
  let itemDialogOpen = $state(false);
  let itemDialogMode = $state<'add' | 'edit'>('add');
  let itemDialogInitial = $state<ShoppingListItemDto | null>(null);
  let itemDialogSubmitting = $state(false);

  let confirmDeleteOpen = $state(false);
  let pendingDelete = $state<ShoppingListItemDto | null>(null);
  let confirmClearOpen = $state(false);
  let confirmArchiveOpen = $state(false);
  let confirmRegenerateOpen = $state(false);

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

  // Categorized view of the list. svelte-dnd-action requires each category zone
  // to OWN a stable, mutable items array across a drag, so this is $state the
  // drag handlers mutate per-zone — NOT a $derived off list.items. Re-bucketing
  // mid-drag (which a $derived, or any cross-zone rebuild, would do the moment an
  // item's category changes) tears down the dragged DOM node the library is
  // tracking and silently breaks dragging an item BETWEEN categories (within a
  // category still worked). Non-drag changes rebuild it via the effect below;
  // the drag path mutates it directly and never re-buckets until finalize.
  let categories = $state<{ category: string; items: ShoppingListItemDto[] }[]>([]);
  // True only while a drag is in flight (first consider → finalize). Gates the
  // rebuild effect so it can't clobber the zones the drag handlers own.
  let dragging = $state(false);

  function catRank(c: string): number {
    const i = CATEGORY_ORDER.indexOf(c);
    return i === -1 ? 99 : i;
  }

  function rebuildCategories() {
    if (!list) {
      categories = [];
      return;
    }
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
    categories = [...groups.entries()]
      .sort(([a], [b]) => catRank(a) - catRank(b) || a.localeCompare(b))
      .map(([category, items]) => ({ category, items }));
  }

  // Rebuild the categorized view whenever the list data changes (load / add /
  // edit / delete / check re-sort) — but NEVER mid-drag: while `dragging`, the
  // handlers own `categories`, and a rebuild would re-bucket the dragged node
  // and abort a cross-category drop. On finalize the handlers set dragging=false
  // after resyncing list.items, so this fires once to reconcile. $effect.pre
  // (runs before the DOM update) so categories is populated in the same frame
  // list arrives — no empty flash.
  $effect.pre(() => {
    if (dragging) return;
    rebuildCategories();
  });

  /** Retry / reload the current list (the template's Retry button). */
  function loadList(_listId: number | null) {
    void sl.load();
  }

  async function handleToggle(item: ShoppingListItemDto) {
    const target = !item.isChecked;
    const outcome = await sl.setChecked(item, target);
    // Only show undo when CHECKING, and only for a check that went through (matches production).
    if (outcome !== 'ok' || !target) return;
    const { id, name } = item;
    const listId = sl.currentListId;
    showToast({
      message: `Checked ${name}`,
      kind: 'success',
      actionLabel: 'Undo',
      durationMs: 4000,
      onAction: () => {
        // Resolve the row at undo time: a poll or a reorder may have replaced the object (and its token). Item
        // ids are per list, so an undo pressed after switching lists must not touch the other list's item.
        if (sl.currentListId !== listId) return;
        const current = sl.list?.items.find((i) => i.id === id);
        if (!current || !current.isChecked) return;
        void sl.setChecked(current, false);
      },
    });
  }

  async function handleQuantity(item: ShoppingListItemDto, delta: number) {
    await sl.setQuantity(item, delta);
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
      const outcome =
        itemDialogMode === 'add'
          ? await sl.addItem(value)
          : itemDialogInitial
            ? await sl.editItem(itemDialogInitial, value)
            : 'skipped';
      // Close on success. On a 409 (someone else changed the item since this dialog opened) close too: the list
      // refreshes to their version and the user redoes the edit there. Other failures keep the dialog open.
      if (outcome === 'ok' || outcome === 'conflict') itemDialogOpen = false;
    } finally {
      itemDialogSubmitting = false;
    }
  }

  function askDelete(item: ShoppingListItemDto) {
    pendingDelete = item;
    confirmDeleteOpen = true;
  }

  async function handleConfirmDelete() {
    if (!pendingDelete) return;
    const item = pendingDelete;
    confirmDeleteOpen = false;
    try {
      await sl.deleteItem(item);
    } finally {
      pendingDelete = null;
    }
  }

  async function handleToggleFavorite() {
    await sl.toggleFavorite();
  }

  function handleMenuAction(action: MenuAction) {
    switch (action) {
      case 'new': newListOpen = true; break;
      case 'rename': renameOpen = true; break;
      case 'generate': generateOpen = true; break;
      case 'regenerate': confirmRegenerateOpen = true; break;
      case 'clear-checked': confirmClearOpen = true; break;
      case 'archive': confirmArchiveOpen = true; break;
      // A real navigation (goto), not shallow URL sync — /past is its own route with its own island.
      case 'past-lists': void goto(`${base}/shopping-list/past`); break;
    }
  }

  async function handleConfirmRegenerate() {
    confirmRegenerateOpen = false;
    await sl.regenerate();
  }

  async function handleRename(newName: string) {
    renameSubmitting = true;
    try {
      if (await sl.rename(newName)) renameOpen = false;
    } finally {
      renameSubmitting = false;
    }
  }

  async function handleCreateList(name: string) {
    newListSubmitting = true;
    try {
      if (await sl.createList(name)) newListOpen = false;
    } finally {
      newListSubmitting = false;
    }
  }

  async function handleGenerate(body: { startDate: string; endDate: string }) {
    generateSubmitting = true;
    try {
      if (await sl.generate(body)) generateOpen = false;
    } finally {
      generateSubmitting = false;
    }
  }

  async function handleConfirmClearChecked() {
    confirmClearOpen = false;
    await sl.clearChecked();
  }

  async function handleConfirmArchive() {
    confirmArchiveOpen = false;
    await sl.archive();
  }

  async function handleSelectList(id: number) {
    await sl.selectList(id);
  }

  // ── Drag-and-drop reorder ───────────────────────────────────────────────
  //
  // svelte-dnd-action emits `consider` (live during drag) and `finalize`
  // (drop). We optimistically splice the reordered items back into
  // `list.items` with updated sortOrder + category on each event so the
  // categorized view stays coherent while dragging. On finalize we POST
  // the new orderings to the server (inside the list's write window).

  // Replace ONE category zone's items array — the per-zone update
  // svelte-dnd-action hands us on each event. No category mutation and no
  // cross-zone rebuild here: doing either mid-drag re-buckets the dragged node
  // and breaks the drop (see the `categories` comment). Reconciliation is
  // deferred to finalize.
  function setZoneItems(category: string, items: ShoppingListItemDto[]) {
    const zone = categories.find((c) => c.category === category);
    if (zone) zone.items = items;
  }

  async function handleDnd(
    category: string,
    reordered: ShoppingListItemDto[],
    phase: 'consider' | 'finalize',
  ) {
    if (phase === 'consider') {
      // Live drag: reflect the moved item in whichever zone fired, nothing else.
      // (On a cross-category drag both the source and destination zones fire —
      // each updates only its own array, so the dragged node is never torn down.)
      dragging = true;
      setZoneItems(category, reordered);
      return;
    }

    // finalize (drop): commit. Stamp the dropped items with this zone's category
    // + their new order, resync list.items from every zone, THEN release the
    // drag guard so the rebuild effect reconciles once from the final state.
    setZoneItems(category, reordered);
    reordered.forEach((item, index) => {
      item.category = category;
      item.sortOrder = index;
    });
    if (list) list.items = categories.flatMap((c) => c.items);
    dragging = false;

    await sl.reorder(category, reordered);
  }

  $effect(() => {
    // Initial load: the summaries + the selected list (or the top one, when none is selected). untrack: the
    // load reads (and writes) the state's runes synchronously, and this one-time setup must not subscribe to
    // them (memory svelte5-setup-effect-async-loader-loop).
    untrack(() => void sl.load());
    // House liveness (quest 76e6f169 — this island previously refreshed only on
    // visibilitychange): interval poll while visible + immediate refetch on re-show.
    // A poll during a pending write is deferred to its settle; a stale one is dropped.
    const liveness = startLiveness(() => void sl.load());
    return () => liveness.stop();
  });
</script>

<div class="sl-container">
  <HeaderBar
    {lists}
    {currentListId}
    currentListName={list?.name ?? null}
    isFavorite={list?.isFavorite ?? false}
    canRegenerate={list?.hasMealPlan ?? false}
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
      {#each categories as { category, items } (category)}
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

<ConfirmDialog
  open={confirmRegenerateOpen}
  title="Regenerate List"
  message="Rebuild this list from its full linked meal-plan week? Your manual items, checked-off state and quantity edits are kept; generated quantities refresh from the whole week's plan."
  confirmLabel="Regenerate"
  onCancel={() => (confirmRegenerateOpen = false)}
  onConfirm={handleConfirmRegenerate}
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
    bottom: calc(24px + env(safe-area-inset-bottom, 0px));
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
    z-index: 1050;
  }
  /* Mobile: clear the MainLayout bottom nav + safe-area inset. */
  @media (max-width: 960px) {
    .sl-fab {
      bottom: calc(80px + env(safe-area-inset-bottom, 0px));
    }
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
