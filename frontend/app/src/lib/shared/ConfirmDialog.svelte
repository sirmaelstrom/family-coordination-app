<script lang="ts">
  // Canonical confirm dialog — the ONE copy every island route imports.
  //
  // Props are the UNION of all callers:
  //   • shopping-list passes `danger` (boolean) → red confirm button
  //   • admin passes `tone: 'primary' | 'danger'` → green / red confirm button
  //   • settings / recipes / meal-plan pass `cancelLabel` and rely on the confirm colour
  //
  // Colour resolution (so every caller can reproduce its look):
  //   tone === 'primary'          → green (primary)
  //   tone === 'danger' | danger  → red   (error)
  //   otherwise                   → green (primary)  [shopping-list default]
  //
  // NOTE for migration: callers whose OLD dialog defaulted to a red button with
  // no explicit prop (settings/recipes ConfirmDialog defaulted to danger) must
  // pass `tone="danger"` (or `danger`) when they switch to this component, else
  // the confirm button turns green. Their confirms are destructive deletes, so
  // `danger`/`tone="danger"` is the correct, self-documenting call anyway.
  interface Props {
    open: boolean;
    title: string;
    message: string;
    confirmLabel?: string;
    cancelLabel?: string;
    /** shopping-list convention: red confirm button. */
    danger?: boolean;
    /** admin convention: explicit confirm colour (wins over `danger`). */
    tone?: 'danger' | 'primary';
    onCancel: () => void;
    onConfirm: () => void;
  }

  let {
    open,
    title,
    message,
    confirmLabel = 'Confirm',
    cancelLabel = 'Cancel',
    danger = false,
    tone,
    onCancel,
    onConfirm,
  }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);

  const isDanger = $derived(tone === 'danger' || (tone === undefined && danger));

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) dialogEl.showModal();
    else if (!open && dialogEl.open) dialogEl.close();
  });
</script>

<dialog bind:this={dialogEl} onclose={onCancel} class="sh-dialog">
  <div class="sh-body">
    <h2>{title}</h2>
    <p>{message}</p>
    <div class="sh-actions">
      <button type="button" class="sh-btn-ghost" onclick={onCancel}>{cancelLabel}</button>
      <button
        type="button"
        class="sh-btn-primary"
        class:danger={isDanger}
        onclick={onConfirm}
      >
        {confirmLabel}
      </button>
    </div>
  </div>
</dialog>

<style>
  .sh-dialog {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(400px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
  }
  .sh-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .sh-body {
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
    font-size: 0.875rem;
  }
  .sh-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 8px;
  }
  .sh-btn-ghost,
  .sh-btn-primary {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
  }
  .sh-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .sh-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .sh-btn-primary {
    background: var(--color-primary);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .sh-btn-primary:hover {
    background: var(--color-primary-hover);
  }
  .sh-btn-primary.danger {
    background: var(--color-error);
  }
  .sh-btn-primary.danger:hover {
    filter: brightness(0.95);
  }
</style>
