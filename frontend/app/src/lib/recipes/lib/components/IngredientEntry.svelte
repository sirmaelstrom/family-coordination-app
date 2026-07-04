<script lang="ts">
  // Ingredient entry (mirrors IngredientEntry.razor): a raw NL input that parses
  // on Enter/Tab/blur via POST /parse-ingredient, revealing editable qty / unit /
  // name / category / notes fields. Unit autocomplete is a static list; the
  // ingredient-name autocomplete pulls server suggestions (≥2 chars). A "Bulk
  // Paste" button opens the bulk dialog (hosted by EditApp).
  import type { NewIngredientInput } from '../recipeEditStore.svelte';
  import { parseIngredient, ingredientSuggestions } from '../api';
  import { showToast } from '$lib/shared/toast-store.svelte';

  interface Props {
    categories: string[];
    onAdd: (input: NewIngredientInput) => void;
    onBulkOpen: () => void;
  }

  let { categories, onAdd, onBulkOpen }: Props = $props();

  const UNITS = [
    'cup', 'cups', 'tbsp', 'tsp', 'oz', 'lb', 'lbs', 'g', 'kg', 'ml', 'l',
    'clove', 'cloves', 'can', 'cans', 'bunch', 'slice', 'slices', 'piece', 'pieces',
  ];

  let rawInput = $state('');
  let showParsed = $state(false);
  let quantity = $state('');
  let unit = $state('');
  let name = $state('');
  let category = $state('Pantry');
  let notes = $state('');
  let parsing = $state(false);

  let nameSuggestions = $state<string[]>([]);
  let suggestTimer: ReturnType<typeof setTimeout> | null = null;

  function findCategory(suggested: string): string {
    const match = categories.find((c) => c.toLowerCase() === suggested.toLowerCase());
    return match ?? categories[0] ?? 'Pantry';
  }

  async function parseInput(): Promise<void> {
    if (!rawInput.trim() || parsing) return;
    parsing = true;
    try {
      const parsed = await parseIngredient(rawInput);
      quantity = parsed.quantity != null ? String(parsed.quantity) : '';
      unit = parsed.unit ?? '';
      name = parsed.name;
      notes = parsed.notes ?? '';
      category = findCategory(parsed.suggestedCategory);
      showParsed = true;
    } catch {
      showToast({ message: "Couldn't parse that ingredient.", kind: 'error' });
    } finally {
      parsing = false;
    }
  }

  async function add(): Promise<void> {
    if (!showParsed) {
      await parseInput();
      if (!showParsed) return;
    }
    if (!name.trim()) return;
    const q = quantity.trim() === '' ? null : Number(quantity);
    onAdd({
      name: name.trim(),
      quantity: q != null && !Number.isNaN(q) ? q : null,
      unit: unit.trim() === '' ? null : unit.trim(),
      category,
      notes: notes.trim() === '' ? null : notes.trim(),
    });
    reset();
  }

  function reset(): void {
    rawInput = '';
    showParsed = false;
    quantity = '';
    unit = '';
    name = '';
    category = categories[0] ?? 'Pantry';
    notes = '';
    nameSuggestions = [];
  }

  function handleKeyDown(e: KeyboardEvent): void {
    if (e.key === 'Enter') {
      e.preventDefault();
      if (showParsed) void add();
      else if (rawInput.trim()) void parseInput();
    } else if (e.key === 'Tab' && !showParsed && rawInput.trim()) {
      void parseInput();
    }
  }

  function handleRawBlur(): void {
    if (!showParsed && rawInput.trim()) void parseInput();
  }

  function onNameInput(value: string): void {
    name = value;
    if (suggestTimer) clearTimeout(suggestTimer);
    if (value.trim().length < 2) {
      nameSuggestions = [];
      return;
    }
    suggestTimer = setTimeout(async () => {
      try {
        nameSuggestions = await ingredientSuggestions(value.trim());
      } catch {
        nameSuggestions = [];
      }
    }, 300);
  }
</script>

<section class="rc-paper">
  <div class="rc-entry-head">
    <h3 class="rc-entry-title">Add Ingredients</h3>
    <button type="button" class="rc-text-btn" onclick={onBulkOpen}>⧉ Bulk Paste</button>
  </div>

  <div class="rc-raw-row">
    <input
      type="text"
      class="rc-input"
      placeholder="Add ingredient (e.g., '2 cups flour' or '1/2 lb chicken, boneless')"
      bind:value={rawInput}
      onkeydown={handleKeyDown}
      onblur={handleRawBlur}
    />
    <button type="button" class="rc-icon-add" aria-label="Parse / add" onclick={add}>＋</button>
  </div>

  {#if showParsed}
    <div class="rc-parsed-grid">
      <label class="rc-field rc-col-qty">
        <span class="rc-field-label">Quantity</span>
        <input type="text" class="rc-input" bind:value={quantity} />
      </label>
      <label class="rc-field rc-col-unit">
        <span class="rc-field-label">Unit</span>
        <input type="text" class="rc-input" list="rc-unit-list" bind:value={unit} />
        <datalist id="rc-unit-list">
          {#each UNITS as u (u)}
            <option value={u}></option>
          {/each}
        </datalist>
      </label>
      <label class="rc-field rc-col-name">
        <span class="rc-field-label">Ingredient</span>
        <input
          type="text"
          class="rc-input"
          list="rc-name-list"
          value={name}
          oninput={(e) => onNameInput(e.currentTarget.value)}
        />
        <datalist id="rc-name-list">
          {#each nameSuggestions as s (s)}
            <option value={s}></option>
          {/each}
        </datalist>
      </label>
      <label class="rc-field rc-col-cat">
        <span class="rc-field-label">Category</span>
        <select class="rc-input" bind:value={category}>
          {#each categories as cat (cat)}
            <option value={cat}>{cat}</option>
          {/each}
        </select>
      </label>
      <div class="rc-col-add">
        <button type="button" class="rc-btn-solid" onclick={add}>Add</button>
      </div>
      <label class="rc-field rc-col-notes">
        <span class="rc-field-label">Notes (optional)</span>
        <input type="text" class="rc-input" bind:value={notes} />
      </label>
    </div>
  {/if}
</section>

<style>
  .rc-paper {
    background: var(--color-surface);
    border: 1px solid var(--color-line);
    border-radius: var(--radius-md);
    box-shadow: var(--shadow-1);
    padding: 16px;
    margin-bottom: 16px;
  }
  .rc-entry-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    margin-bottom: 12px;
  }
  .rc-entry-title {
    margin: 0;
    font-size: 1rem;
    font-weight: 500;
  }
  .rc-text-btn {
    font: inherit;
    background: transparent;
    border: none;
    color: var(--color-primary);
    cursor: pointer;
    font-size: 0.875rem;
    font-weight: 500;
  }
  .rc-text-btn:hover {
    text-decoration: underline;
  }
  .rc-raw-row {
    display: flex;
    gap: 8px;
    align-items: stretch;
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
  .rc-icon-add {
    flex-shrink: 0;
    width: 44px;
    border: 1px solid var(--color-primary);
    background: transparent;
    color: var(--color-primary);
    border-radius: var(--radius-sm);
    font-size: 1.3rem;
    line-height: 1;
    cursor: pointer;
  }
  .rc-icon-add:hover {
    background: var(--color-action-hover);
  }
  .rc-parsed-grid {
    display: grid;
    grid-template-columns: repeat(12, 1fr);
    gap: 12px;
    margin-top: 12px;
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
  .rc-col-qty {
    grid-column: span 2;
  }
  .rc-col-unit {
    grid-column: span 2;
  }
  .rc-col-name {
    grid-column: span 4;
  }
  .rc-col-cat {
    grid-column: span 2;
  }
  .rc-col-add {
    grid-column: span 2;
    display: flex;
    align-items: flex-end;
  }
  .rc-col-notes {
    grid-column: span 12;
  }
  @media (max-width: 700px) {
    .rc-col-qty,
    .rc-col-unit,
    .rc-col-name,
    .rc-col-cat,
    .rc-col-add,
    .rc-col-notes {
      grid-column: span 12;
    }
  }
  .rc-btn-solid {
    font: inherit;
    width: 100%;
    padding: 10px 16px;
    border: none;
    border-radius: var(--radius-sm);
    background: var(--color-primary);
    color: #fff;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
  }
  .rc-btn-solid:hover {
    background: var(--color-primary-hover);
  }
</style>
