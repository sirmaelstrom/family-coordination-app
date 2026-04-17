<script lang="ts">
  interface Props {
    open: boolean;
    title: string;
    message: string;
    confirmLabel?: string;
    danger?: boolean;
    onCancel: () => void;
    onConfirm: () => void;
  }

  let {
    open,
    title,
    message,
    confirmLabel = 'Confirm',
    danger = false,
    onCancel,
    onConfirm,
  }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) dialogEl.showModal();
    else if (!open && dialogEl.open) dialogEl.close();
  });
</script>

<dialog bind:this={dialogEl} onclose={onCancel} class="sl-dialog">
  <div class="sl-body">
    <h2>{title}</h2>
    <p>{message}</p>
    <div class="sl-actions">
      <button type="button" class="sl-btn-ghost" onclick={onCancel}>Cancel</button>
      <button
        type="button"
        class="sl-btn-primary"
        class:danger
        onclick={onConfirm}
      >
        {confirmLabel}
      </button>
    </div>
  </div>
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
  .sl-body {
    padding: 24px;
    display: flex;
    flex-direction: column;
    gap: 12px;
  }
  h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  p {
    margin: 0;
    color: var(--color-text-muted);
    line-height: 1.5;
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
  }
  .sl-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .sl-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .sl-btn-primary {
    background: var(--color-primary);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .sl-btn-primary:hover {
    background: var(--color-primary-hover);
  }
  .sl-btn-primary.danger {
    background: var(--color-error);
  }
  .sl-btn-primary.danger:hover {
    background: rgb(198, 40, 40);
  }
</style>
