<script lang="ts">
  // Reject-with-reason dialog (parity RejectReasonDialog.razor). The reason is
  // OPTIONAL (R-C7) — submitting with an empty box is allowed (the server does
  // not 400). MaxLength 500, mirroring the MudTextField.
  interface Props {
    open: boolean;
    requestorName: string;
    householdName: string;
    onCancel: () => void;
    onConfirm: (reason: string) => void;
  }

  let { open, requestorName, householdName, onCancel, onConfirm }: Props = $props();

  let dialogEl: HTMLDialogElement | null = $state(null);
  let reason = $state('');

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      reason = ''; // fresh each time it opens
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  function submit() {
    onConfirm(reason.trim());
  }
</script>

<dialog bind:this={dialogEl} onclose={onCancel} class="adm-reject">
  {#if open}
    <div class="adm-reject-body">
      <h2>Reject Request</h2>
      <p class="adm-reject-msg">
        Reject household request “<strong>{householdName}</strong>” from <strong>{requestorName}</strong>?
      </p>
      <label class="adm-field">
        <span class="adm-field-label">Reason (optional)</span>
        <textarea
          class="adm-textarea"
          rows="3"
          maxlength="500"
          placeholder="Why is this request being rejected?"
          bind:value={reason}
        ></textarea>
      </label>
      <div class="adm-reject-actions">
        <button type="button" class="adm-btn-ghost" onclick={onCancel}>Cancel</button>
        <button type="button" class="adm-btn-danger" onclick={submit}>Reject</button>
      </div>
    </div>
  {/if}
</dialog>

<style>
  .adm-reject[open] {
    position: fixed;
    inset: 0;
    margin: auto;
    border: none;
    border-radius: var(--radius-md);
    background: var(--color-surface);
    color: var(--color-text);
    padding: 0;
    width: min(440px, calc(100vw - 32px));
    box-shadow: var(--shadow-4);
  }
  .adm-reject::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .adm-reject-body {
    display: flex;
    flex-direction: column;
    gap: 12px;
    padding: 24px;
  }
  .adm-reject h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .adm-reject-msg {
    margin: 0;
    font-size: 0.9375rem;
  }
  .adm-field {
    display: flex;
    flex-direction: column;
    gap: 6px;
  }
  .adm-field-label {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .adm-textarea {
    font: inherit;
    padding: 10px 12px;
    border-radius: var(--radius-sm);
    border: 1px solid var(--color-line-strong);
    background: var(--color-surface);
    color: var(--color-text);
    resize: vertical;
    min-height: 72px;
  }
  .adm-textarea:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
  }
  .adm-reject-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    margin-top: 4px;
  }
  .adm-btn-ghost,
  .adm-btn-danger {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 40px;
    font-weight: 500;
  }
  .adm-btn-ghost {
    background: transparent;
    color: var(--color-text-muted);
  }
  .adm-btn-ghost:hover {
    background: var(--color-action-hover);
  }
  .adm-btn-danger {
    background: var(--color-error);
    color: #fff;
  }
  .adm-btn-danger:hover {
    filter: brightness(0.95);
  }
</style>
