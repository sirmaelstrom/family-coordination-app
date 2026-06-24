<script lang="ts">
  // ───────────────────────────────────────────────────────────────────────
  // A tiny confirm dialog — replaces the Blazor DialogService.ShowMessageBox
  // used to confirm a meal removal ("Remove this meal from MMM d?"). Hosted in
  // App.svelte; opened with a message + a confirm callback.
  // ───────────────────────────────────────────────────────────────────────
  interface Props {
    open: boolean;
    title: string;
    message: string;
    confirmLabel?: string;
    cancelLabel?: string;
    onCancel: () => void;
    onConfirm: () => void;
  }

  let {
    open,
    title,
    message,
    confirmLabel = 'Remove',
    cancelLabel = 'Cancel',
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

<dialog bind:this={dialogEl} onclose={onCancel} class="mp-confirm">
  {#if open}
    <div class="mp-confirm-body">
      <h2>{title}</h2>
      <p class="mp-confirm-msg">{message}</p>
      <div class="mp-confirm-actions">
        <button type="button" class="mp-btn-ghost" onclick={onCancel}>{cancelLabel}</button>
        <button type="button" class="mp-btn-solid" onclick={onConfirm}>{confirmLabel}</button>
      </div>
    </div>
  {/if}
</dialog>

<style>
  .mp-confirm[open] {
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
  .mp-confirm::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .mp-confirm-body {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 24px;
  }
  .mp-confirm h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .mp-confirm-msg {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-text-muted);
  }
  .mp-confirm-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
  .mp-btn-ghost,
  .mp-btn-solid {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
  }
  .mp-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .mp-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .mp-btn-solid {
    background: var(--color-primary);
    color: #fff;
  }
  .mp-btn-solid:hover {
    background: var(--color-primary-hover);
  }
</style>
