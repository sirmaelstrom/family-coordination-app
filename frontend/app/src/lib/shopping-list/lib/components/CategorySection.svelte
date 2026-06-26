<script lang="ts">
  import type { ShoppingListItemDto } from '../types';
  import ItemRow from './ItemRow.svelte';
  import { dndzone, type DndEvent } from 'svelte-dnd-action';

  interface Props {
    category: string;
    items: ShoppingListItemDto[];
    pendingIds: Set<number>;
    dragEnabled: boolean;
    onToggle: (item: ShoppingListItemDto) => void;
    onQuantity: (item: ShoppingListItemDto, delta: number) => void;
    onEdit: (item: ShoppingListItemDto) => void;
    onDelete: (item: ShoppingListItemDto) => void;
    onDnd: (
      category: string,
      items: ShoppingListItemDto[],
      phase: 'consider' | 'finalize',
    ) => void;
  }

  let {
    category,
    items,
    pendingIds,
    dragEnabled,
    onToggle,
    onQuantity,
    onEdit,
    onDelete,
    onDnd,
  }: Props = $props();

  let collapsed = $state(false);
  let uncheckedCount = $derived(items.filter((i) => !i.isChecked).length);

  function handleConsider(e: CustomEvent<DndEvent<ShoppingListItemDto>>) {
    onDnd(category, e.detail.items, 'consider');
  }
  function handleFinalize(e: CustomEvent<DndEvent<ShoppingListItemDto>>) {
    onDnd(category, e.detail.items, 'finalize');
  }
</script>

<section class="sl-category">
  <button
    type="button"
    class="sl-category-header"
    onclick={() => (collapsed = !collapsed)}
    aria-expanded={!collapsed}
  >
    <span class="sl-category-title">{category}</span>
    <span class="sl-category-right">
      <span class="sl-category-count">{uncheckedCount}</span>
      <svg class="sl-chev" class:open={!collapsed} viewBox="0 0 24 24" width="20" height="20">
        <path d="M7 10l5 5 5-5z" fill="currentColor" />
      </svg>
    </span>
  </button>
  {#if !collapsed}
    {#if dragEnabled}
      <div
        class="sl-category-body"
        use:dndzone={{
          items,
          type: 'shopping-list-items',
          flipDurationMs: 150,
          dropTargetStyle: {},
          dragDisabled: false,
          delayTouchStart: 250,
        }}
        onconsider={handleConsider}
        onfinalize={handleFinalize}
      >
        {#each items as item (item.id)}
          <div>
            <ItemRow
              {item}
              pending={pendingIds.has(item.id)}
              draggable
              {onToggle}
              {onQuantity}
              {onEdit}
              {onDelete}
            />
          </div>
        {/each}
      </div>
    {:else}
      <div class="sl-category-body">
        {#each items as item (item.id)}
          <ItemRow
            {item}
            pending={pendingIds.has(item.id)}
            draggable={false}
            {onToggle}
            {onQuantity}
            {onEdit}
            {onDelete}
          />
        {/each}
      </div>
    {/if}
  {/if}
</section>

<style>
  .sl-category {
    background: var(--color-surface);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-2);
    margin-bottom: 16px;
    overflow: hidden;
  }
  .sl-category-header {
    display: flex;
    justify-content: space-between;
    align-items: center;
    width: 100%;
    padding: 16px;
    min-height: 56px;
    background: transparent;
    border: none;
    color: inherit;
    font: inherit;
    cursor: pointer;
    -webkit-tap-highlight-color: transparent;
  }
  .sl-category-header:hover {
    background: var(--color-action-hover);
  }
  .sl-category-title {
    font-size: 1.125rem;
    font-weight: 500;
  }
  .sl-category-right {
    display: inline-flex;
    align-items: center;
    gap: 8px;
    color: var(--color-text-muted);
  }
  .sl-category-count {
    display: inline-flex;
    align-items: center;
    justify-content: center;
    min-width: 24px;
    height: 24px;
    padding: 0 8px;
    border-radius: 12px;
    background: var(--color-action-hover);
    font-size: 0.75rem;
    font-weight: 500;
  }
  .sl-chev {
    transition: transform 0.2s;
  }
  .sl-chev.open {
    transform: rotate(180deg);
  }
  .sl-category-body {
    display: flex;
    flex-direction: column;
    min-height: 8px;
  }
</style>
