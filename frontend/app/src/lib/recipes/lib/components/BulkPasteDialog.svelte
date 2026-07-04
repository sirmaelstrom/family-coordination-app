<script lang="ts">
  // Bulk-paste dialog (mirrors BulkPasteDialog.razor): a multi-line textarea →
  // POST /parse-ingredients (one round-trip) → a preview list (qty/unit/name/notes
  // + an incomplete-parse warning) → import all into the edit form.
  import type { NewIngredientInput } from '../recipeEditStore.svelte';
  import type { ParsedIngredientDto } from '../types';
  import { parseIngredients } from '../api';

  interface Props {
    open: boolean;
    categories: string[];
    onClose: () => void;
    onImport: (rows: NewIngredientInput[]) => void;
  }

  let { open, categories, onClose, onImport }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);
  let pastedText = $state('');
  let parsed = $state<ParsedIngredientDto[]>([]);
  let parsing = $state(false);
  let parseTimer: ReturnType<typeof setTimeout> | null = null;

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) dialogEl.showModal();
    else if (!open && dialogEl.open) dialogEl.close();
  });

  function findCategory(suggested: string): string {
    const match = categories.find((c) => c.toLowerCase() === suggested.toLowerCase());
    return match ?? categories[0] ?? 'Pantry';
  }

  function onText(value: string): void {
    pastedText = value;
    if (parseTimer) clearTimeout(parseTimer);
    const lines = value
      .split(/\r?\n/)
      .map((l) => l.trim())
      .filter((l) => l.length > 0);
    if (lines.length === 0) {
      parsed = [];
      return;
    }
    parseTimer = setTimeout(async () => {
      parsing = true;
      try {
        parsed = await parseIngredients(lines);
      } catch {
        parsed = [];
      } finally {
        parsing = false;
      }
    }, 300);
  }

  function removeRow(index: number): void {
    parsed = parsed.filter((_, i) => i !== index);
  }

  function reset(): void {
    pastedText = '';
    parsed = [];
    parsing = false;
  }

  function handleClose(): void {
    reset();
    onClose();
  }

  function importAll(): void {
    const rows: NewIngredientInput[] = parsed.map((p) => ({
      name: p.name,
      quantity: p.quantity,
      unit: p.unit,
      category: findCategory(p.suggestedCategory),
      notes: p.notes,
    }));
    onImport(rows);
    reset();
    onClose();
  }
</script>

<dialog bind:this={dialogEl} onclose={handleClose} class="rc-dialog">
  {#if open}
    <header class="rc-dialog-head">
      <h2>Bulk Paste Ingredients</h2>
      <button type="button" class="rc-dialog-x" aria-label="Close" onclick={handleClose}>×</button>
    </header>

    <div class="rc-dialog-body">
      <p class="rc-bulk-intro">
        Paste multiple ingredients (one per line). Each line is parsed into structured fields.
      </p>
      <textarea
        class="rc-input rc-bulk-textarea"
        rows="8"
        placeholder="2 cups flour&#10;1/2 lb chicken, boneless&#10;3 cloves garlic, minced"
        value={pastedText}
        oninput={(e) => onText(e.currentTarget.value)}
      ></textarea>

      {#if parsing}
        <p class="rc-bulk-status">Parsing…</p>
      {:else if parsed.length > 0}
        <p class="rc-bulk-status">Parsed Ingredients ({parsed.length})</p>
        <div class="rc-bulk-list">
          {#each parsed as p, i (i)}
            <div class="rc-bulk-row">
              <div class="rc-bulk-row-head">
                <span class="rc-bulk-name">{p.name}</span>
                <button type="button" class="rc-bulk-del" aria-label="Remove" onclick={() => removeRow(i)}
                  >×</button
                >
              </div>
              <div class="rc-bulk-fields">
                <span><strong>Qty:</strong> {p.quantity ?? '—'}</span>
                <span><strong>Unit:</strong> {p.unit ?? '—'}</span>
              </div>
              {#if p.notes}
                <div class="rc-bulk-note"><strong>Notes:</strong> {p.notes}</div>
              {/if}
              {#if !p.isComplete}
                <div class="rc-bulk-warn">Incomplete parse — review and edit after import.</div>
              {/if}
            </div>
          {/each}
        </div>
      {/if}
    </div>

    <footer class="rc-dialog-actions">
      <button type="button" class="rc-btn-ghost" onclick={handleClose}>Cancel</button>
      <button type="button" class="rc-btn-solid" onclick={importAll} disabled={parsed.length === 0}>
        Import {parsed.length} Ingredient{parsed.length !== 1 ? 's' : ''}
      </button>
    </footer>
  {/if}
</dialog>

<style>
  .rc-dialog[open] {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(600px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
    display: flex;
    flex-direction: column;
  }
  .rc-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .rc-dialog-head {
    display: flex;
    align-items: center;
    gap: 12px;
    padding: 16px 16px 12px 24px;
    border-bottom: 1px solid var(--color-line);
  }
  .rc-dialog-head h2 {
    margin: 0;
    flex: 1;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .rc-dialog-x {
    flex-shrink: 0;
    width: 36px;
    height: 36px;
    display: grid;
    place-items: center;
    border: none;
    background: transparent;
    color: var(--color-text-muted);
    border-radius: var(--radius-sm);
    font-size: 1.4rem;
    line-height: 1;
    cursor: pointer;
  }
  .rc-dialog-x:hover {
    background: var(--color-action-hover);
    color: var(--color-text);
  }
  .rc-dialog-body {
    overflow-y: auto;
    padding: 16px 24px;
    flex: 1;
  }
  .rc-bulk-intro {
    margin: 0 0 12px;
    font-size: 0.9375rem;
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
  .rc-bulk-textarea {
    resize: vertical;
    min-height: 140px;
  }
  .rc-bulk-status {
    margin: 16px 0 8px;
    font-size: 0.875rem;
    font-weight: 500;
  }
  .rc-bulk-list {
    display: flex;
    flex-direction: column;
    gap: 8px;
    max-height: 360px;
    overflow-y: auto;
  }
  .rc-bulk-row {
    padding: 8px 12px;
    border: 1px solid var(--color-line);
    border-radius: var(--radius-sm);
    background: var(--color-background);
  }
  .rc-bulk-row-head {
    display: flex;
    align-items: center;
    justify-content: space-between;
    gap: 8px;
  }
  .rc-bulk-name {
    font-weight: 500;
  }
  .rc-bulk-del {
    border: none;
    background: transparent;
    color: var(--color-error);
    font-size: 1.2rem;
    line-height: 1;
    cursor: pointer;
  }
  .rc-bulk-fields {
    display: flex;
    gap: 16px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
    margin-top: 4px;
  }
  .rc-bulk-note {
    font-size: 0.8125rem;
    margin-top: 4px;
  }
  .rc-bulk-warn {
    margin-top: 6px;
    padding: 6px 10px;
    border-radius: var(--radius-sm);
    background: rgba(251, 140, 0, 0.12);
    color: var(--color-warning);
    font-size: 0.8125rem;
  }
  .rc-dialog-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 12px 24px calc(16px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }
  .rc-btn-ghost,
  .rc-btn-solid {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
  }
  .rc-btn-ghost {
    background: transparent;
    color: var(--color-text-muted);
  }
  .rc-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .rc-btn-solid {
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-solid:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .rc-btn-solid:disabled {
    opacity: 0.6;
    cursor: default;
  }
</style>
