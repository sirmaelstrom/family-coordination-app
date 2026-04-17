<script lang="ts">
  import type { ShoppingListItemDto } from '../types';
  import UserAvatar from './UserAvatar.svelte';

  interface Props {
    item: ShoppingListItemDto;
    pending: boolean;
    draggable: boolean;
    onToggle: (item: ShoppingListItemDto) => void;
    onQuantity: (item: ShoppingListItemDto, delta: number) => void;
    onEdit: (item: ShoppingListItemDto) => void;
    onDelete: (item: ShoppingListItemDto) => void;
  }

  let { item, pending, draggable, onToggle, onQuantity, onEdit, onDelete }: Props = $props();

  function formatQuantity(q: number): string {
    if (q === Math.floor(q)) return q.toFixed(0);
    return q.toFixed(2).replace(/0+$/, '').replace(/\.$/, '');
  }
</script>

<div class="sl-item-row" class:checked={item.isChecked} class:pending>
  {#if draggable}
    <span class="sl-drag-handle" aria-label="Drag to reorder" title="Drag to reorder">
      <svg viewBox="0 0 24 24" width="18" height="18">
        <path
          d="M9 4h2v2H9zM13 4h2v2h-2zM9 11h2v2H9zM13 11h2v2h-2zM9 18h2v2H9zM13 18h2v2h-2z"
          fill="currentColor"
        />
      </svg>
    </span>
  {/if}

  <button
    type="button"
    class="sl-checkbox"
    onclick={() => onToggle(item)}
    aria-pressed={item.isChecked}
    aria-label={item.isChecked ? 'Uncheck item' : 'Check item'}
  >
    {#if item.isChecked}
      <svg viewBox="0 0 24 24" width="18" height="18">
        <path d="M9 16.2 4.8 12l-1.4 1.4L9 19 21 7l-1.4-1.4L9 16.2z" fill="currentColor" />
      </svg>
    {/if}
  </button>

  {#if item.quantity != null}
    <div class="sl-qty" role="group" aria-label="Adjust quantity">
      <button
        type="button"
        class="sl-qty-btn"
        onclick={() => onQuantity(item, -1)}
        disabled={pending || (item.quantity ?? 0) <= 1}
        aria-label="Decrease quantity"
      >
        <svg viewBox="0 0 24 24" width="18" height="18"><path d="M19 13H5v-2h14v2z" fill="currentColor" /></svg>
      </button>
      <span class="sl-qty-value">{formatQuantity(item.quantity)}</span>
      {#if item.unit}
        <span class="sl-qty-unit">{item.unit}</span>
      {/if}
      <button
        type="button"
        class="sl-qty-btn"
        onclick={() => onQuantity(item, 1)}
        disabled={pending}
        aria-label="Increase quantity"
      >
        <svg viewBox="0 0 24 24" width="18" height="18"><path d="M19 13h-6v6h-2v-6H5v-2h6V5h2v6h6v2z" fill="currentColor" /></svg>
      </button>
    </div>
  {/if}

  <button
    type="button"
    class="sl-body"
    onclick={() => onToggle(item)}
    aria-label="Toggle {item.name}"
  >
    <span class="sl-name">{item.name}</span>
  </button>

  {#if item.addedByName}
    <UserAvatar
      name={item.addedByName}
      initials={item.addedByInitials}
      pictureUrl={item.addedByPictureUrl}
      size={24}
    />
  {/if}

  <div class="sl-row-actions">
    <button
      type="button"
      class="sl-icon-btn"
      onclick={() => onEdit(item)}
      aria-label="Edit item"
      title="Edit"
    >
      <svg viewBox="0 0 24 24" width="18" height="18">
        <path d="M3 17.25V21h3.75L17.81 9.94l-3.75-3.75L3 17.25zM20.71 7.04a1 1 0 0 0 0-1.41l-2.34-2.34a1 1 0 0 0-1.41 0l-1.83 1.83 3.75 3.75 1.83-1.83z" fill="currentColor" />
      </svg>
    </button>
    <button
      type="button"
      class="sl-icon-btn sl-icon-btn-danger"
      onclick={() => onDelete(item)}
      aria-label="Delete item"
      title="Delete"
    >
      <svg viewBox="0 0 24 24" width="18" height="18">
        <path d="M6 19a2 2 0 0 0 2 2h8a2 2 0 0 0 2-2V7H6v12zM19 4h-3.5l-1-1h-5l-1 1H5v2h14V4z" fill="currentColor" />
      </svg>
    </button>
  </div>
</div>

<style>
  .sl-item-row {
    display: flex;
    align-items: center;
    gap: 8px;
    padding: 8px 16px;
    min-height: 56px;
    border-bottom: 1px solid var(--color-line);
    transition: background-color 0.15s;
    background: var(--color-surface);
  }
  .sl-item-row:last-child {
    border-bottom: none;
  }
  .sl-item-row:hover {
    background: var(--color-action-hover);
  }
  .sl-item-row.pending {
    opacity: 0.6;
  }

  .sl-drag-handle {
    flex-shrink: 0;
    display: inline-flex;
    align-items: center;
    justify-content: center;
    width: 24px;
    height: 32px;
    color: var(--color-text-disabled);
    cursor: grab;
    -webkit-tap-highlight-color: transparent;
  }
  .sl-drag-handle:active {
    cursor: grabbing;
  }

  .sl-checkbox {
    flex-shrink: 0;
    width: 28px;
    height: 28px;
    padding: 0;
    border: 2px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: transparent;
    display: grid;
    place-items: center;
    color: #fff;
    cursor: pointer;
    transition:
      background-color 0.15s,
      border-color 0.15s;
    -webkit-tap-highlight-color: transparent;
  }
  .sl-item-row.checked .sl-checkbox {
    background: var(--color-primary);
    border-color: var(--color-primary);
  }

  .sl-qty {
    display: inline-flex;
    align-items: center;
    gap: 2px;
    flex-shrink: 0;
  }
  .sl-qty-btn {
    width: 32px;
    height: 32px;
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
  .sl-qty-btn:hover:not(:disabled) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .sl-qty-btn:disabled {
    color: var(--color-action-disabled);
    cursor: default;
  }
  .sl-qty-value {
    font-weight: 600;
    min-width: 20px;
    text-align: center;
    font-size: 0.9375rem;
  }
  .sl-qty-unit {
    color: var(--color-text-muted);
    font-size: 0.875rem;
    margin-right: 4px;
  }

  .sl-body {
    flex: 1;
    min-width: 0;
    padding: 8px 4px;
    background: transparent;
    border: none;
    color: inherit;
    font: inherit;
    text-align: left;
    cursor: pointer;
    display: flex;
    flex-direction: column;
    gap: 2px;
    -webkit-tap-highlight-color: transparent;
  }
  .sl-name {
    font-size: 1rem;
    line-height: 1.4;
    overflow: hidden;
    text-overflow: ellipsis;
  }
  .sl-item-row.checked .sl-name {
    text-decoration: line-through;
    color: var(--color-text-muted);
  }

  .sl-row-actions {
    display: flex;
    gap: 2px;
    flex-shrink: 0;
    opacity: 0;
    transition: opacity 0.15s;
  }
  .sl-item-row:hover .sl-row-actions,
  .sl-item-row:focus-within .sl-row-actions {
    opacity: 1;
  }
  @media (pointer: coarse) {
    .sl-row-actions {
      opacity: 1;
    }
  }
  .sl-icon-btn {
    width: 36px;
    height: 36px;
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
  .sl-icon-btn-danger:hover {
    color: var(--color-error);
  }
</style>
