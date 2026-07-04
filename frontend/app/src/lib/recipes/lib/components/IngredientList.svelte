<script lang="ts">
  // Ingredient list (mirrors IngredientList.razor): drag-reorder via
  // svelte-dnd-action (the chores pattern), edit (remove + re-add), and
  // delete-with-undo (the undo lives on the toast). Quantities use the EXACT
  // formatter (formatExactQuantity — entered values are precise).
  import type { IngredientRow } from '../recipeEditStore.svelte';
  import { formatExactQuantity } from '../quantity';
  import { dndzone, type DndEvent } from 'svelte-dnd-action';

  interface Props {
    ingredients: IngredientRow[];
    onReorder: (rows: IngredientRow[]) => void;
    onEdit: (id: number) => void;
    onRemove: (id: number) => void;
  }

  let { ingredients, onReorder, onEdit, onRemove }: Props = $props();

  // Local copy svelte-dnd-action mutates during a drag; resynced from the source
  // whenever we're NOT dragging (chores pattern — avoids fighting the library).
  let rows = $state<IngredientRow[]>([]);
  let dragging = $state(false);
  $effect(() => {
    if (!dragging) rows = [...ingredients];
  });

  function handleConsider(e: CustomEvent<DndEvent<IngredientRow>>): void {
    dragging = true;
    rows = e.detail.items;
  }
  function handleFinalize(e: CustomEvent<DndEvent<IngredientRow>>): void {
    rows = e.detail.items;
    onReorder([...rows]);
    dragging = false;
  }

  function formatLine(ing: IngredientRow): string {
    const parts: string[] = [];
    if (ing.quantity != null) parts.push(formatExactQuantity(ing.quantity));
    if (ing.unit) parts.push(ing.unit);
    parts.push(ing.name);
    return parts.join(' ');
  }

  function categoryColor(category: string): string {
    switch (category) {
      case 'Meat':
        return 'var(--color-error)';
      case 'Produce':
        return 'var(--color-success)';
      case 'Dairy':
        return 'var(--color-warning)';
      case 'Spices':
        return 'var(--color-secondary)';
      case 'Frozen':
        return 'var(--color-info)';
      case 'Beverages':
        return 'var(--color-primary)';
      default:
        return 'var(--color-text-muted)';
    }
  }
</script>

<h3 class="rc-list-title">Ingredients ({ingredients.length})</h3>

{#if ingredients.length === 0}
  <div class="rc-list-empty">No ingredients yet. Use the form above to add ingredients.</div>
{:else}
  <ul
    class="rc-ing-list"
    use:dndzone={{ items: rows, flipDurationMs: 150, dropTargetStyle: {}, delayTouchStart: 250 }}
    onconsider={handleConsider}
    onfinalize={handleFinalize}
  >
    {#each rows as ing (ing.id)}
      <li class="rc-ing-item">
        <div class="rc-ing-main">
          <span class="rc-ing-grip" aria-hidden="true">⠿</span>
          <div class="rc-ing-text">
            <span class="rc-ing-line">{formatLine(ing)}</span>
            {#if ing.notes}
              <span class="rc-ing-notes">{ing.notes}</span>
            {/if}
          </div>
        </div>
        <div class="rc-ing-actions">
          <span class="rc-cat-chip" style="--chip-color: {categoryColor(ing.category)}">{ing.category}</span>
          <button type="button" class="rc-ing-btn" aria-label="Edit ingredient" onclick={() => onEdit(ing.id)}
            >✎</button
          >
          <button
            type="button"
            class="rc-ing-btn rc-ing-del"
            aria-label="Delete ingredient"
            onclick={() => onRemove(ing.id)}>🗑</button
          >
        </div>
      </li>
    {/each}
  </ul>
{/if}

<style>
  .rc-list-title {
    margin: 0 0 8px;
    font-size: 1rem;
    font-weight: 500;
  }
  .rc-list-empty {
    padding: 12px 16px;
    border-radius: var(--radius-sm);
    background: rgba(30, 136, 229, 0.08);
    border-left: 4px solid var(--color-info);
    font-size: 0.875rem;
    color: var(--color-text);
  }
  .rc-ing-list {
    list-style: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
    min-height: 60px;
  }
  .rc-ing-item {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 12px;
    padding: 12px;
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm);
    box-shadow: var(--shadow-1);
    cursor: grab;
  }
  .rc-ing-item:active {
    cursor: grabbing;
  }
  .rc-ing-main {
    display: flex;
    align-items: center;
    gap: 8px;
    min-width: 0;
  }
  .rc-ing-grip {
    color: var(--color-text-disabled);
    cursor: grab;
  }
  .rc-ing-text {
    display: flex;
    flex-direction: column;
    min-width: 0;
  }
  .rc-ing-line {
    overflow-wrap: anywhere;
  }
  .rc-ing-notes {
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .rc-ing-actions {
    display: flex;
    align-items: center;
    gap: 4px;
    flex-shrink: 0;
  }
  .rc-cat-chip {
    font-size: 0.6875rem;
    font-weight: 500;
    padding: 2px 10px;
    border-radius: 12px;
    border: 1px solid var(--chip-color);
    color: var(--chip-color);
    white-space: nowrap;
  }
  .rc-ing-btn {
    width: 32px;
    height: 32px;
    display: grid;
    place-items: center;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    cursor: pointer;
    font-size: 0.95rem;
  }
  .rc-ing-btn:hover {
    background: var(--color-action-hover);
  }
  .rc-ing-del:hover {
    color: var(--color-error);
  }
</style>
