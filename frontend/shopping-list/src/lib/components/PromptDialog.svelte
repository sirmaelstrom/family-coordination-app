<script lang="ts">
  interface Props {
    open: boolean;
    title: string;
    label: string;
    initial?: string;
    confirmLabel?: string;
    submitting?: boolean;
    onClose: () => void;
    onSubmit: (value: string) => Promise<void> | void;
  }

  let {
    open,
    title,
    label,
    initial = '',
    confirmLabel = 'Save',
    submitting = false,
    onClose,
    onSubmit,
  }: Props = $props();

  let value = $state('');
  let dialogEl: HTMLDialogElement | null = $state(null);
  let inputEl: HTMLInputElement | null = $state(null);

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      value = initial;
      dialogEl.showModal();
      queueMicrotask(() => inputEl?.select());
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    const v = value.trim();
    if (!v || submitting) return;
    await onSubmit(v);
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="sl-dialog">
  <form method="dialog" onsubmit={handleSubmit}>
    <h2>{title}</h2>
    <label>
      <span>{label}</span>
      <input bind:this={inputEl} type="text" bind:value required autocomplete="off" />
    </label>
    <div class="sl-actions">
      <button type="button" class="sl-btn-ghost" onclick={onClose} disabled={submitting}>Cancel</button>
      <button type="submit" class="sl-btn-primary" disabled={submitting || !value.trim()}>
        {submitting ? 'Saving…' : confirmLabel}
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
    width: min(380px, calc(100vw - 32px));
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
  input {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
  }
  input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .sl-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
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
