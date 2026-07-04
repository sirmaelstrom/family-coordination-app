<script lang="ts">
  // In-island edit dialog — replaces the Blazor CategoryEditDialog MudDialog.
  // Parity: plain freeform name + emoji text fields + a color input (the emoji is
  // freeform text today, e.g. "meat_on_bone"; no emoji picker — that'd be new UX).
  import type { CategoryDto, CategoryWriteRequest } from '../types';

  interface Props {
    open: boolean;
    category: CategoryDto | null;
    onCancel: () => void;
    onSave: (body: CategoryWriteRequest) => void;
  }

  let { open, category, onCancel, onSave }: Props = $props();

  let dialogEl = $state<HTMLDialogElement | null>(null);
  let name = $state('');
  let iconEmoji = $state('');
  let color = $state('#808080');

  // Seed the form from the category when the dialog opens. Reads open/category
  // (props) and writes the form $state — it does NOT read the form fields, so
  // there's no self-triggering loop.
  $effect(() => {
    if (open && category) {
      name = category.name;
      iconEmoji = category.iconEmoji ?? '';
      color = category.color || '#808080';
    }
  });

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) dialogEl.showModal();
    else if (!open && dialogEl.open) dialogEl.close();
  });

  function save() {
    if (!name.trim()) return;
    onSave({ name: name.trim(), iconEmoji: iconEmoji.trim() || null, color });
  }
</script>

<dialog bind:this={dialogEl} onclose={onCancel} class="rc-confirm">
  {#if open}
    <div class="rc-confirm-body">
      <h2>Edit Category</h2>
      <label class="set-field">
        <span>Name</span>
        <input type="text" bind:value={name} class="set-input" />
      </label>
      <label class="set-field">
        <span>Emoji</span>
        <input type="text" bind:value={iconEmoji} class="set-input" placeholder="e.g., meat_on_bone" />
      </label>
      <label class="set-field">
        <span>Color</span>
        <input type="color" bind:value={color} class="set-color" />
      </label>
      <div class="rc-confirm-actions">
        <button type="button" class="rc-btn-ghost" onclick={onCancel}>Cancel</button>
        <button type="button" class="rc-btn-primary" onclick={save} disabled={!name.trim()}>Save</button>
      </div>
    </div>
  {/if}
</dialog>

<style>
  .rc-confirm[open] {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(420px, calc(100vw - 32px));
    box-shadow: var(--shadow-4);
  }
  .rc-confirm::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .rc-confirm-body {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 24px;
  }
  .rc-confirm h2 {
    margin: 0 0 4px;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .set-field {
    display: flex;
    flex-direction: column;
    gap: 4px;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .set-input {
    font: inherit;
    padding: 10px 12px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--color-line-strong);
    background: var(--color-surface);
    color: var(--color-text);
  }
  .set-input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
  }
  .set-color {
    width: 64px;
    height: 40px;
    padding: 2px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    cursor: pointer;
  }
  .rc-confirm-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
  .rc-btn-ghost,
  .rc-btn-primary {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
  }
  .rc-btn-ghost {
    background: transparent;
    color: var(--color-text-muted);
  }
  .rc-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .rc-btn-primary {
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-primary:hover {
    background: var(--color-primary-hover);
  }
  .rc-btn-primary:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
