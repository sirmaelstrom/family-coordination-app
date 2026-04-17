<script lang="ts">
  import { STANDARD_CATEGORIES } from '../api';
  import type { ShoppingListItemDto } from '../types';

  export interface ItemFormValue {
    name: string;
    quantity: number | null;
    unit: string | null;
    category: string;
  }

  interface Props {
    open: boolean;
    mode: 'add' | 'edit';
    submitting: boolean;
    initial?: ShoppingListItemDto | null;
    onClose: () => void;
    onSubmit: (value: ItemFormValue) => Promise<void>;
  }

  let { open, mode, submitting, initial = null, onClose, onSubmit }: Props = $props();

  let name = $state('');
  // Keep quantity as a string so <input type="number"> coercion doesn't break
  // trim/compare logic. We normalize to number | null at submit time.
  let quantity = $state('');
  let unit = $state('');
  let category = $state<string>('Pantry');

  let dialogEl: HTMLDialogElement | null = $state(null);
  let nameInput: HTMLInputElement | null = $state(null);

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      // Seed form from initial when opening in edit mode.
      if (mode === 'edit' && initial) {
        name = initial.name;
        quantity = initial.quantity == null ? '' : String(initial.quantity);
        unit = initial.unit ?? '';
        category = initial.category || 'Pantry';
      } else {
        name = '';
        quantity = '';
        unit = '';
        category = 'Pantry';
      }
      dialogEl.showModal();
      queueMicrotask(() => nameInput?.focus());
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    if (!name.trim() || submitting) return;
    // <input> bindings can return string | number | null depending on the
    // input type; normalize defensively rather than trusting our declared type.
    const qRaw = quantity as unknown;
    let qNum: number | null = null;
    if (typeof qRaw === 'number' && Number.isFinite(qRaw)) {
      qNum = qRaw;
    } else if (typeof qRaw === 'string' && qRaw.trim() !== '') {
      const parsed = Number(qRaw);
      qNum = Number.isFinite(parsed) ? parsed : null;
    }
    const value: ItemFormValue = {
      name: name.trim(),
      quantity: qNum,
      unit: typeof unit === 'string' && unit.trim() ? unit.trim() : null,
      category,
    };
    await onSubmit(value);
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="sl-dialog">
  <form method="dialog" onsubmit={handleSubmit}>
    <h2>{mode === 'edit' ? 'Edit Item' : 'Add Item'}</h2>

    <label>
      <span>Name</span>
      <input
        bind:this={nameInput}
        type="text"
        bind:value={name}
        required
        autocomplete="off"
        placeholder="e.g. Milk"
      />
    </label>

    <div class="sl-row-2">
      <label>
        <span>Quantity</span>
        <input
          type="text"
          inputmode="decimal"
          pattern="[0-9]*\.?[0-9]*"
          bind:value={quantity}
          placeholder="1"
        />
      </label>
      <label>
        <span>Unit</span>
        <input type="text" bind:value={unit} autocomplete="off" placeholder="e.g. lb, box" />
      </label>
    </div>

    <label>
      <span>Category</span>
      <select bind:value={category}>
        {#each STANDARD_CATEGORIES as c}
          <option value={c}>{c}</option>
        {/each}
      </select>
    </label>

    <div class="sl-actions">
      <button type="button" class="sl-btn-ghost" onclick={onClose} disabled={submitting}>
        Cancel
      </button>
      <button type="submit" class="sl-btn-primary" disabled={submitting || !name.trim()}>
        {submitting ? 'Saving…' : mode === 'edit' ? 'Save' : 'Add'}
      </button>
    </div>
  </form>
</dialog>

<style>
  .sl-dialog {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(440px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
  }
  .sl-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .sl-dialog form {
    display: flex;
    flex-direction: column;
    gap: 16px;
    padding: 24px;
  }
  .sl-dialog h2 {
    margin: 0 0 4px;
    font-size: 1.25rem;
    font-weight: 500;
  }
  label {
    display: flex;
    flex-direction: column;
    gap: 6px;
    font-size: 0.875rem;
  }
  label > span {
    color: var(--color-text-muted);
  }
  input,
  select {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
  }
  input:focus,
  select:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .sl-row-2 {
    display: grid;
    grid-template-columns: 1fr 1fr;
    gap: 12px;
  }
  .sl-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
  .sl-btn-ghost,
  .sl-btn-primary {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
    letter-spacing: 0.02em;
  }
  .sl-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .sl-btn-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .sl-btn-primary {
    background: var(--color-primary);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .sl-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .sl-btn-primary:disabled,
  .sl-btn-ghost:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
