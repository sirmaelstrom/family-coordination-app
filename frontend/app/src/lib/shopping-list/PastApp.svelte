<script lang="ts">
  // Past shopping lists (quest f63bb90a): browse archived lists, reopen, delete, and pick items
  // off into an active list. Read-only by design — editing means reopening. Identity comes from
  // the canonical $lib/session store via ctx (M8); this island never fetches /api/me.
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import type { ShellContext } from '$lib/session.svelte';
  import type {
    ArchivedListSummaryDto,
    ShoppingListDto,
    ShoppingListSummaryDto,
  } from './lib/types';
  import {
    addItem,
    deleteList,
    getArchivedList,
    listArchived,
    listLists,
    restoreList,
  } from './lib/api';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';
  import { showToast } from '$lib/shared/toast-store.svelte';

  interface Props {
    ctx: ShellContext;
  }

  // ctx is required by the route contract (M8) even though this read-only surface renders no
  // identity of its own yet.
  let {}: Props = $props();

  let archived = $state<ArchivedListSummaryDto[]>([]);
  let activeLists = $state<ShoppingListSummaryDto[]>([]);
  let favoritesOnly = $state(false);
  let loading = $state(true);
  let error = $state<string | null>(null);

  /** The expanded list's read-only detail, or null when collapsed. */
  let detail = $state<ShoppingListDto | null>(null);
  let detailLoading = $state(false);
  let pickedIds = $state(new Set<number>());
  let targetListId = $state<number | null>(null);
  let adding = $state(false);

  let confirmDeleteId = $state<number | null>(null);

  /** Stale-response guard (house pattern): only the latest load may commit. */
  let loadSeq = 0;

  async function load(): Promise<void> {
    const seq = ++loadSeq;
    try {
      loading = true;
      error = null;
      const [past, active] = await Promise.all([listArchived(favoritesOnly), listLists()]);
      if (seq !== loadSeq) return;
      archived = past;
      activeLists = active;
      // Favorites-first is the server's sort for actives too — default the pick target to the top.
      if (targetListId == null || !active.some((l) => l.id === targetListId)) {
        targetListId = active[0]?.id ?? null;
      }
    } catch (e) {
      if (seq !== loadSeq) return;
      error = e instanceof Error ? e.message : String(e);
    } finally {
      if (seq === loadSeq) loading = false;
    }
  }

  async function toggleDetail(listId: number): Promise<void> {
    if (detail?.id === listId) {
      detail = null;
      pickedIds = new Set();
      return;
    }
    detailLoading = true;
    try {
      detail = await getArchivedList(listId);
      pickedIds = new Set();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      detailLoading = false;
    }
  }

  function togglePick(itemId: number): void {
    const next = new Set(pickedIds);
    if (next.has(itemId)) next.delete(itemId);
    else next.add(itemId);
    pickedIds = next;
  }

  async function handleReopen(listId: number): Promise<void> {
    try {
      await restoreList(listId);
      showToast({ message: 'List reopened', kind: 'success' });
      void goto(`${base}/shopping-list/${listId}`);
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }

  async function handleConfirmDelete(): Promise<void> {
    const id = confirmDeleteId;
    confirmDeleteId = null;
    if (id == null) return;
    try {
      await deleteList(id);
      if (detail?.id === id) detail = null;
      showToast({ message: 'List permanently deleted', kind: 'info' });
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    }
  }

  /** Copy picked rows into the target ACTIVE list via AddItem — they arrive manual + unchecked. */
  async function handleAddPicked(): Promise<void> {
    if (!detail || targetListId == null || pickedIds.size === 0) return;
    adding = true;
    const picked = detail.items.filter((i) => pickedIds.has(i.id));
    try {
      for (const item of picked) {
        await addItem(targetListId, {
          name: item.name,
          quantity: item.quantity,
          unit: item.unit,
          category: item.category,
        });
      }
      const target = activeLists.find((l) => l.id === targetListId);
      showToast({
        message: `Added ${picked.length} item${picked.length === 1 ? '' : 's'} to ${target?.name ?? 'the list'}`,
        kind: 'success',
      });
      pickedIds = new Set();
      await load();
    } catch (e) {
      error = e instanceof Error ? e.message : String(e);
    } finally {
      adding = false;
    }
  }

  function formatDate(iso: string): string {
    // Full ISO instant → local date (never new Date('YYYY-MM-DD') — that parses UTC-midnight).
    return new Date(iso).toLocaleDateString(undefined, {
      year: 'numeric',
      month: 'short',
      day: 'numeric',
    });
  }

  $effect(() => {
    void favoritesOnly; // re-run on filter flips
    void load();
  });
</script>

<div class="pl-container">
  <header class="pl-header">
    <div class="pl-title-row">
      <a class="pl-back" href="{base}/shopping-list" aria-label="Back to shopping list">←</a>
      <h1 class="pl-title">Past Lists</h1>
    </div>
    <label class="pl-filter">
      <input type="checkbox" bind:checked={favoritesOnly} />
      Favorites only
    </label>
  </header>

  {#if error}
    <div class="pl-error" role="alert">
      <span>{error}</span>
      <button type="button" onclick={() => load()}>Retry</button>
    </div>
  {/if}

  {#if loading}
    <p class="pl-status">Loading…</p>
  {:else if archived.length === 0}
    <p class="pl-status">
      {favoritesOnly ? 'No favorite past lists.' : 'No past lists yet — archived lists land here.'}
    </p>
  {:else}
    <ul class="pl-list">
      {#each archived as row (row.id)}
        <li class="pl-row">
          <button
            type="button"
            class="pl-row-main"
            aria-expanded={detail?.id === row.id}
            onclick={() => toggleDetail(row.id)}
          >
            <span class="pl-row-name">
              {row.isFavorite ? '★ ' : ''}{row.name}
            </span>
            <span class="pl-row-meta">
              {formatDate(row.createdAt)} · {row.itemCount} item{row.itemCount === 1 ? '' : 's'}
              {#if row.hasMealPlan}· from meal plan{/if}
            </span>
          </button>
          <div class="pl-row-actions">
            <button type="button" class="pl-btn" onclick={() => handleReopen(row.id)}>
              Reopen
            </button>
            <button
              type="button"
              class="pl-btn pl-btn-danger"
              onclick={() => (confirmDeleteId = row.id)}
            >
              Delete
            </button>
          </div>

          {#if detail?.id === row.id}
            <div class="pl-detail">
              {#if detail.items.length === 0}
                <p class="pl-status">This list has no items.</p>
              {:else}
                <ul class="pl-items">
                  {#each detail.items as item (item.id)}
                    <li>
                      <label class="pl-item">
                        <input
                          type="checkbox"
                          checked={pickedIds.has(item.id)}
                          onchange={() => togglePick(item.id)}
                        />
                        <span class="pl-item-name" class:checked={item.isChecked}>
                          {item.name}
                        </span>
                        {#if item.quantity != null}
                          <span class="pl-item-qty">{item.quantity} {item.unit ?? ''}</span>
                        {/if}
                        <span class="pl-item-cat">{item.category}</span>
                      </label>
                    </li>
                  {/each}
                </ul>
                <div class="pl-pick-bar">
                  {#if activeLists.length > 0}
                    <label class="pl-target">
                      Add to
                      <select bind:value={targetListId}>
                        {#each activeLists as target (target.id)}
                          <option value={target.id}>
                            {target.isFavorite ? '★ ' : ''}{target.name}
                          </option>
                        {/each}
                      </select>
                    </label>
                    <button
                      type="button"
                      class="pl-btn pl-btn-primary"
                      disabled={pickedIds.size === 0 || adding}
                      onclick={handleAddPicked}
                    >
                      {adding ? 'Adding…' : `Add ${pickedIds.size || ''} picked`}
                    </button>
                  {:else}
                    <span class="pl-status">No active list to add to — reopen this one instead.</span>
                  {/if}
                </div>
              {/if}
            </div>
          {/if}
        </li>
      {/each}
    </ul>
  {/if}

  {#if detailLoading}
    <p class="pl-status">Loading list…</p>
  {/if}
</div>

<ConfirmDialog
  open={confirmDeleteId != null}
  title="Delete List Permanently"
  message={`Permanently delete ${archived.find((l) => l.id === confirmDeleteId)?.name ?? 'this list'}? This cannot be undone.`}
  confirmLabel="Delete forever"
  danger
  onCancel={() => (confirmDeleteId = null)}
  onConfirm={handleConfirmDelete}
/>

<style>
  .pl-container {
    max-width: 720px;
    margin: 0 auto;
    padding: 16px;
  }
  .pl-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-wrap: wrap;
    gap: 12px;
    margin-bottom: 24px;
  }
  .pl-title-row {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .pl-back {
    font-size: 1.5rem;
    text-decoration: none;
    color: var(--color-text);
  }
  .pl-title {
    font-size: 2.125rem;
    font-weight: 400;
    margin: 0;
    color: var(--color-text);
  }
  .pl-filter {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--color-text);
    cursor: pointer;
  }
  .pl-status {
    color: var(--color-text-muted, #666);
    padding: 8px 0;
  }
  .pl-error {
    display: flex;
    justify-content: space-between;
    align-items: center;
    gap: 12px;
    padding: 10px 12px;
    margin-bottom: 16px;
    border: 1px solid var(--color-danger, #c62828);
    border-radius: 8px;
    color: var(--color-danger, #c62828);
  }
  .pl-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .pl-row {
    border: 1px solid var(--color-border, #ddd);
    border-radius: 10px;
    padding: 10px 12px;
    display: grid;
    grid-template-columns: 1fr auto;
    gap: 6px 12px;
    align-items: center;
  }
  .pl-row-main {
    text-align: left;
    background: none;
    border: none;
    padding: 0;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    gap: 2px;
    color: var(--color-text);
  }
  .pl-row-name {
    font-weight: 500;
  }
  .pl-row-meta {
    font-size: 0.85rem;
    color: var(--color-text-muted, #666);
  }
  .pl-row-actions {
    display: flex;
    gap: 8px;
  }
  .pl-btn {
    border: 1px solid var(--color-border, #ccc);
    background: none;
    border-radius: 8px;
    padding: 6px 12px;
    cursor: pointer;
    color: var(--color-text);
  }
  .pl-btn-danger {
    color: var(--color-danger, #c62828);
    border-color: var(--color-danger, #c62828);
  }
  .pl-btn-primary {
    color: var(--color-primary, #1565c0);
    border-color: var(--color-primary, #1565c0);
  }
  .pl-btn:disabled {
    opacity: 0.5;
    cursor: default;
  }
  .pl-detail {
    grid-column: 1 / -1;
    border-top: 1px solid var(--color-border, #eee);
    padding-top: 8px;
  }
  .pl-items {
    list-style: none;
    margin: 0;
    padding: 0;
  }
  .pl-item {
    display: flex;
    align-items: center;
    gap: 10px;
    padding: 4px 0;
    cursor: pointer;
    color: var(--color-text);
  }
  .pl-item-name.checked {
    text-decoration: line-through;
    color: var(--color-text-muted, #888);
  }
  .pl-item-qty {
    font-size: 0.85rem;
    color: var(--color-text-muted, #666);
  }
  .pl-item-cat {
    margin-left: auto;
    font-size: 0.75rem;
    color: var(--color-text-muted, #999);
  }
  .pl-pick-bar {
    display: flex;
    align-items: center;
    gap: 12px;
    margin-top: 10px;
    flex-wrap: wrap;
  }
  .pl-target {
    display: flex;
    align-items: center;
    gap: 6px;
    color: var(--color-text);
  }
</style>
