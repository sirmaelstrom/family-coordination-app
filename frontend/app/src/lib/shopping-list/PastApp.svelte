<script lang="ts">
  // Past shopping lists (quest f63bb90a): browse archived lists, reopen, delete, and pick items
  // off into an active list. Read-only by design — editing means reopening. Identity comes from
  // the canonical $lib/session store via ctx (M8); this island never fetches /api/me.
  // The state and its load/write ordering live in PastListsState (quest 41337b6f): two Resources,
  // and a delete that runs inside both their windows.
  import { base } from '$app/paths';
  import { goto } from '$app/navigation';
  import type { ShellContext } from '$lib/session.svelte';
  import { PastListsState } from './lib/pastListsState.svelte';
  import ConfirmDialog from '$lib/shared/ConfirmDialog.svelte';

  interface Props {
    ctx: ShellContext;
  }

  // ctx is required by the route contract (M8) even though this read-only surface renders no
  // identity of its own yet.
  let {}: Props = $props();

  const past = new PastListsState();
  let confirmDeleteId = $state<number | null>(null);

  async function handleReopen(listId: number): Promise<void> {
    if (await past.reopen(listId)) void goto(`${base}/shopping-list/${listId}`);
  }

  async function handleConfirmDelete(): Promise<void> {
    const id = confirmDeleteId;
    confirmDeleteId = null;
    if (id == null) return;
    await past.remove(id);
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
    void past.favoritesOnly; // re-run on filter flips
    void past.load();
  });
</script>

<div class="pl-container">
  <header class="pl-header">
    <div class="pl-title-row">
      <a class="pl-back" href="{base}/shopping-list" aria-label="Back to shopping list">←</a>
      <h1 class="pl-title">Past Lists</h1>
    </div>
    <label class="pl-filter">
      <input type="checkbox" bind:checked={past.favoritesOnly} />
      Favorites only
    </label>
  </header>

  {#if past.error}
    <div class="pl-error" role="alert">
      <span>{past.error}</span>
      <button type="button" onclick={() => past.load()}>Retry</button>
    </div>
  {/if}

  {#if past.loading}
    <p class="pl-status">Loading…</p>
  {:else if past.archived.length === 0}
    <p class="pl-status">
      {past.favoritesOnly ? 'No favorite past lists.' : 'No past lists yet — archived lists land here.'}
    </p>
  {:else}
    <ul class="pl-list">
      {#each past.archived as row (row.id)}
        <li class="pl-row">
          <button
            type="button"
            class="pl-row-main"
            aria-expanded={past.detail?.id === row.id}
            onclick={() => past.toggleDetail(row.id)}
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

          {#if past.detail?.id === row.id}
            <div class="pl-detail">
              {#if past.detail.items.length === 0}
                <p class="pl-status">This list has no items.</p>
              {:else}
                <ul class="pl-items">
                  {#each past.detail.items as item (item.id)}
                    <li>
                      <label class="pl-item">
                        <input
                          type="checkbox"
                          checked={past.pickedIds.has(item.id)}
                          onchange={() => past.togglePick(item.id)}
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
                  {#if past.activeLists.length > 0}
                    <label class="pl-target">
                      Add to
                      <select bind:value={past.targetListId}>
                        {#each past.activeLists as target (target.id)}
                          <option value={target.id}>
                            {target.isFavorite ? '★ ' : ''}{target.name}
                          </option>
                        {/each}
                      </select>
                    </label>
                    <button
                      type="button"
                      class="pl-btn pl-btn-primary"
                      disabled={past.pickedIds.size === 0 || past.adding}
                      onclick={() => past.addPicked()}
                    >
                      {past.adding ? 'Adding…' : `Add ${past.pickedIds.size || ''} picked`}
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

  {#if past.detailLoading}
    <p class="pl-status">Loading list…</p>
  {/if}
</div>

<ConfirmDialog
  open={confirmDeleteId != null}
  title="Delete List Permanently"
  message={`Permanently delete ${past.archived.find((l) => l.id === confirmDeleteId)?.name ?? 'this list'}? This cannot be undone.`}
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
