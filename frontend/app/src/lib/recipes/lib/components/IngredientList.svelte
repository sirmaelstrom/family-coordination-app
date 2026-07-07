<script lang="ts">
  // Ingredient list (mirrors IngredientList.razor): drag-reorder via
  // svelte-dnd-action (the chores pattern), INLINE edit (the row swaps to the
  // same qty / unit / name / category / notes fields the add-entry collects,
  // with save/cancel — replaces the old remove-and-re-add flow), and
  // delete-with-undo (the undo lives on the toast). Quantities use the EXACT
  // formatter (formatExactQuantity — entered values are precise). Dragging is
  // disabled while a row is being edited (typing in inputs must not start a drag).
  import type { IngredientRow, IngredientUpdate } from '../recipeEditStore.svelte';
  import { formatExactQuantity } from '../quantity';
  import { dndzone, type DndEvent } from 'svelte-dnd-action';

  interface Props {
    ingredients: IngredientRow[];
    categories: string[];
    onReorder: (rows: IngredientRow[]) => void;
    onUpdate: (id: number, patch: IngredientUpdate) => void;
    onRemove: (id: number) => void;
  }

  let { ingredients, categories, onReorder, onUpdate, onRemove }: Props = $props();

  // Same static unit list as the add-entry (IngredientEntry).
  const UNITS = [
    'cup', 'cups', 'tbsp', 'tsp', 'oz', 'lb', 'lbs', 'g', 'kg', 'ml', 'l',
    'clove', 'cloves', 'can', 'cans', 'bunch', 'slice', 'slices', 'piece', 'pieces',
  ];

  // Local copy svelte-dnd-action mutates during a drag; resynced from the source
  // whenever we're NOT dragging (chores pattern — avoids fighting the library).
  let rows = $state<IngredientRow[]>([]);
  let dragging = $state(false);
  $effect(() => {
    if (!dragging) rows = [...ingredients];
  });

  // ── Inline edit state (one row at a time; fields are strings while editing) ──
  let editingId = $state<number | null>(null);
  let editQuantity = $state('');
  let editUnit = $state('');
  let editName = $state('');
  let editCategory = $state('Pantry');
  let editNotes = $state('');

  function startEdit(ing: IngredientRow): void {
    editingId = ing.id;
    editQuantity = ing.quantity != null ? String(ing.quantity) : '';
    editUnit = ing.unit ?? '';
    editName = ing.name;
    editCategory = ing.category;
    editNotes = ing.notes ?? '';
  }

  function cancelEdit(): void {
    editingId = null;
  }

  function saveEdit(): void {
    if (editingId == null || !editName.trim()) return;
    const q = editQuantity.trim() === '' ? null : Number(editQuantity);
    onUpdate(editingId, {
      name: editName.trim(),
      quantity: q != null && !Number.isNaN(q) ? q : null,
      unit: editUnit.trim() === '' ? null : editUnit.trim(),
      category: editCategory,
      notes: editNotes.trim() === '' ? null : editNotes.trim(),
    });
    editingId = null;
  }

  function handleEditKeydown(e: KeyboardEvent): void {
    if (e.key === 'Enter') {
      e.preventDefault();
      saveEdit();
    } else if (e.key === 'Escape') {
      e.preventDefault();
      cancelEdit();
    }
  }

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
    use:dndzone={{
      items: rows,
      flipDurationMs: 150,
      dropTargetStyle: {},
      delayTouchStart: 250,
      dragDisabled: editingId != null,
    }}
    onconsider={handleConsider}
    onfinalize={handleFinalize}
  >
    {#each rows as ing (ing.id)}
      <li class="rc-ing-item" class:rc-ing-editing={ing.id === editingId}>
        {#if ing.id === editingId}
          <div class="rc-ing-edit-grid">
            <label class="rc-field rc-col-qty">
              <span class="rc-field-label">Quantity</span>
              <input type="text" class="rc-input" bind:value={editQuantity} onkeydown={handleEditKeydown} />
            </label>
            <label class="rc-field rc-col-unit">
              <span class="rc-field-label">Unit</span>
              <input
                type="text"
                class="rc-input"
                list="rc-il-unit-list"
                bind:value={editUnit}
                onkeydown={handleEditKeydown}
              />
              <datalist id="rc-il-unit-list">
                {#each UNITS as u (u)}
                  <option value={u}></option>
                {/each}
              </datalist>
            </label>
            <label class="rc-field rc-col-name">
              <span class="rc-field-label">Ingredient</span>
              <input type="text" class="rc-input" bind:value={editName} onkeydown={handleEditKeydown} />
            </label>
            <label class="rc-field rc-col-cat">
              <span class="rc-field-label">Category</span>
              <select class="rc-input" bind:value={editCategory}>
                {#each categories as cat (cat)}
                  <option value={cat}>{cat}</option>
                {/each}
                {#if !categories.includes(editCategory)}
                  <option value={editCategory}>{editCategory}</option>
                {/if}
              </select>
            </label>
            <label class="rc-field rc-col-notes">
              <span class="rc-field-label">Notes (optional)</span>
              <input type="text" class="rc-input" bind:value={editNotes} onkeydown={handleEditKeydown} />
            </label>
            <div class="rc-col-edit-actions">
              <button type="button" class="rc-btn-ghost-sm" onclick={cancelEdit}>Cancel</button>
              <button type="button" class="rc-btn-solid-sm" onclick={saveEdit} disabled={!editName.trim()}>
                Save
              </button>
            </div>
          </div>
        {:else}
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
            <button type="button" class="rc-ing-btn" aria-label="Edit ingredient" onclick={() => startEdit(ing)}
              >✎</button
            >
            <button
              type="button"
              class="rc-ing-btn rc-ing-del"
              aria-label="Delete ingredient"
              onclick={() => onRemove(ing.id)}>🗑</button
            >
          </div>
        {/if}
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
  .rc-ing-editing {
    cursor: default;
    border-color: var(--color-primary);
  }
  .rc-ing-editing:active {
    cursor: default;
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

  /* ── Inline edit grid (mirrors IngredientEntry's rc-parsed-grid) ── */
  .rc-ing-edit-grid {
    display: grid;
    grid-template-columns: repeat(12, 1fr);
    gap: 12px;
    width: 100%;
  }
  .rc-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
  }
  .rc-field-label {
    font-size: 0.75rem;
    color: var(--color-text-muted);
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
  .rc-col-qty {
    grid-column: span 2;
  }
  .rc-col-unit {
    grid-column: span 2;
  }
  .rc-col-name {
    grid-column: span 5;
  }
  .rc-col-cat {
    grid-column: span 3;
  }
  .rc-col-notes {
    grid-column: span 8;
  }
  .rc-col-edit-actions {
    grid-column: span 4;
    display: flex;
    align-items: flex-end;
    justify-content: flex-end;
    gap: 8px;
  }
  @media (max-width: 700px) {
    .rc-col-qty,
    .rc-col-unit,
    .rc-col-name,
    .rc-col-cat,
    .rc-col-notes,
    .rc-col-edit-actions {
      grid-column: span 12;
    }
  }
  .rc-btn-ghost-sm,
  .rc-btn-solid-sm {
    font: inherit;
    font-size: 0.875rem;
    font-weight: 500;
    padding: 8px 16px;
    border-radius: var(--radius-sm);
    cursor: pointer;
    min-height: 40px;
  }
  .rc-btn-ghost-sm {
    border: none;
    background: transparent;
    color: var(--color-text-muted);
  }
  .rc-btn-ghost-sm:hover {
    background: var(--color-action-hover);
  }
  .rc-btn-solid-sm {
    border: none;
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-solid-sm:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .rc-btn-solid-sm:disabled {
    opacity: 0.6;
    cursor: default;
  }
</style>
