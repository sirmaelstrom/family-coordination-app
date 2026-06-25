<script lang="ts">
  // A tiny confirm dialog — replaces the Blazor delete-confirm MudDialog.
  // Hosted in ListApp; opened with a message + a confirm callback.
  interface Props {
    open: boolean;
    title: string;
    message: string;
    confirmLabel?: string;
    cancelLabel?: string;
    /** 'danger' (default, red) for destructive confirms; 'primary' (green) for approve. */
    tone?: 'danger' | 'primary';
    onCancel: () => void;
    onConfirm: () => void;
  }

  let {
    open,
    title,
    message,
    confirmLabel = 'Delete',
    cancelLabel = 'Cancel',
    tone = 'danger',
    onCancel,
    onConfirm,
  }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });
</script>

<dialog bind:this={dialogEl} onclose={onCancel} class="rc-confirm">
  {#if open}
    <div class="rc-confirm-body">
      <h2>{title}</h2>
      <p class="rc-confirm-msg">{message}</p>
      <div class="rc-confirm-actions">
        <button type="button" class="rc-btn-ghost" onclick={onCancel}>{cancelLabel}</button>
        <button
          type="button"
          class={tone === 'primary' ? 'rc-btn-confirm-primary' : 'rc-btn-danger'}
          onclick={onConfirm}
        >{confirmLabel}</button>
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
    width: min(400px, calc(100vw - 32px));
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
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .rc-confirm-msg {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .rc-confirm-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
  .rc-btn-ghost,
  .rc-btn-danger,
  .rc-btn-confirm-primary {
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
  .rc-btn-danger {
    background: var(--color-error);
    color: #fff;
  }
  .rc-btn-danger:hover {
    filter: brightness(0.95);
  }
  .rc-btn-confirm-primary {
    background: var(--color-primary);
    color: #fff;
  }
  .rc-btn-confirm-primary:hover {
    background: var(--color-primary-hover);
  }
</style>
