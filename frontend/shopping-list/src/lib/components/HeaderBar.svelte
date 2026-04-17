<script lang="ts">
  import type { ShoppingListSummaryDto } from '../types';

  interface Props {
    lists: ShoppingListSummaryDto[];
    currentListId: number | null;
    currentListName: string | null;
    isFavorite: boolean;
    onSelect: (listId: number) => void;
    onToggleFavorite: () => void;
    onMenuAction: (action: MenuAction) => void;
  }

  export type MenuAction =
    | 'new'
    | 'rename'
    | 'generate'
    | 'clear-checked'
    | 'archive';

  let {
    lists,
    currentListId,
    currentListName,
    isFavorite,
    onSelect,
    onToggleFavorite,
    onMenuAction,
  }: Props = $props();

  let menuOpen = $state(false);
  let menuAnchor: HTMLButtonElement | null = $state(null);

  function handleSelect(e: Event) {
    const v = (e.target as HTMLSelectElement).value;
    const id = Number(v);
    if (Number.isFinite(id)) onSelect(id);
  }

  function handleMenuClick(action: MenuAction) {
    menuOpen = false;
    onMenuAction(action);
  }

  function handleOutsideClick(e: MouseEvent) {
    if (!menuOpen) return;
    const target = e.target as Node;
    if (menuAnchor?.contains(target)) return;
    const menu = document.querySelector('[data-sl-menu]');
    if (menu?.contains(target)) return;
    menuOpen = false;
  }

  $effect(() => {
    if (menuOpen) {
      document.addEventListener('click', handleOutsideClick);
      return () => document.removeEventListener('click', handleOutsideClick);
    }
  });
</script>

<header class="sl-header">
  <h1 class="sl-title">Shopping List</h1>

  <div class="sl-controls">
    {#if lists.length > 1}
      <label class="sl-select-wrap">
        <span class="sl-select-label">List</span>
        <select
          class="sl-select"
          value={currentListId ?? ''}
          onchange={handleSelect}
          aria-label="Select shopping list"
        >
          {#each lists as list (list.id)}
            <option value={list.id}>
              {list.isFavorite ? '★ ' : ''}{list.name}
            </option>
          {/each}
        </select>
      </label>
    {:else if currentListName}
      <span class="sl-single-name">{currentListName}</span>
    {/if}

    {#if currentListId != null}
      <button
        type="button"
        class="sl-icon-btn"
        class:active={isFavorite}
        onclick={onToggleFavorite}
        aria-label={isFavorite ? 'Remove from favorites' : 'Add to favorites'}
        title={isFavorite ? 'Remove from favorites' : 'Add to favorites'}
      >
        <svg viewBox="0 0 24 24" width="22" height="22">
          {#if isFavorite}
            <path d="M12 17.27 18.18 21l-1.64-7.03L22 9.24l-7.19-.62L12 2 9.19 8.62 2 9.24l5.46 4.73L5.82 21 12 17.27z" fill="currentColor" />
          {:else}
            <path d="M22 9.24l-7.19-.62L12 2 9.19 8.62 2 9.24l5.46 4.73L5.82 21 12 17.27 18.18 21l-1.64-7.03L22 9.24zM12 15.4l-3.76 2.27 1-4.28-3.32-2.88 4.38-.38L12 6.1l1.71 4.04 4.38.38-3.32 2.88 1 4.28L12 15.4z" fill="currentColor" />
          {/if}
        </svg>
      </button>
    {/if}

    <div class="sl-menu-wrap">
      <button
        type="button"
        class="sl-icon-btn"
        bind:this={menuAnchor}
        onclick={() => (menuOpen = !menuOpen)}
        aria-label="Shopping list menu"
        aria-expanded={menuOpen}
      >
        <svg viewBox="0 0 24 24" width="22" height="22">
          <path d="M12 8a2 2 0 1 0 0-4 2 2 0 0 0 0 4zm0 2a2 2 0 1 0 0 4 2 2 0 0 0 0-4zm0 6a2 2 0 1 0 0 4 2 2 0 0 0 0-4z" fill="currentColor" />
        </svg>
      </button>
      {#if menuOpen}
        <div class="sl-menu" role="menu" data-sl-menu>
          <button type="button" role="menuitem" onclick={() => handleMenuClick('new')}>
            Create New List
          </button>
          <button
            type="button"
            role="menuitem"
            disabled={currentListId == null}
            onclick={() => handleMenuClick('rename')}
          >
            Rename List
          </button>
          <button type="button" role="menuitem" onclick={() => handleMenuClick('generate')}>
            Generate from Meal Plan
          </button>
          <button
            type="button"
            role="menuitem"
            disabled={currentListId == null}
            onclick={() => handleMenuClick('clear-checked')}
          >
            Clear Checked Items
          </button>
          <button
            type="button"
            role="menuitem"
            disabled={currentListId == null}
            onclick={() => handleMenuClick('archive')}
          >
            Archive List
          </button>
        </div>
      {/if}
    </div>
  </div>
</header>

<style>
  .sl-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    flex-wrap: wrap;
    gap: 12px;
    margin-bottom: 24px;
  }
  .sl-title {
    font-size: 2.125rem;
    font-weight: 400;
    margin: 0;
    color: var(--color-text);
  }
  .sl-controls {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    flex-wrap: wrap;
  }
  .sl-select-wrap {
    position: relative;
    display: inline-flex;
    flex-direction: column;
    min-width: 200px;
  }
  .sl-select-label {
    position: absolute;
    top: -8px;
    left: 10px;
    background: var(--color-background);
    padding: 0 4px;
    font-size: 0.75rem;
    color: var(--color-text-muted);
    pointer-events: none;
  }
  .sl-select {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
    cursor: pointer;
  }
  .sl-select:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .sl-single-name {
    color: var(--color-text-muted);
    font-size: 1rem;
  }
  .sl-icon-btn {
    width: 40px;
    height: 40px;
    padding: 0;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: 50%;
    display: grid;
    place-items: center;
    cursor: pointer;
    transition: background-color 0.15s;
    -webkit-tap-highlight-color: transparent;
  }
  .sl-icon-btn:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .sl-icon-btn.active {
    color: var(--color-warning);
  }

  .sl-menu-wrap {
    position: relative;
  }
  .sl-menu {
    position: absolute;
    top: calc(100% + 4px);
    right: 0;
    min-width: 240px;
    background: var(--color-surface);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-4);
    padding: 4px 0;
    z-index: 20;
  }
  .sl-menu button {
    display: block;
    width: 100%;
    text-align: left;
    padding: 10px 16px;
    background: transparent;
    border: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
  }
  .sl-menu button:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .sl-menu button:disabled {
    color: var(--color-text-disabled);
    cursor: not-allowed;
  }
</style>
