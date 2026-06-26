<script lang="ts">
  interface Props {
    open: boolean;
    submitting: boolean;
    onClose: () => void;
    onSubmit: (body: { startDate: string; endDate: string }) => Promise<void>;
  }

  let { open, submitting, onClose, onSubmit }: Props = $props();

  function isoDate(d: Date): string {
    // YYYY-MM-DD without timezone drift.
    const y = d.getFullYear();
    const m = String(d.getMonth() + 1).padStart(2, '0');
    const day = String(d.getDate()).padStart(2, '0');
    return `${y}-${m}-${day}`;
  }

  function mondayOf(d: Date): Date {
    const out = new Date(d);
    const dow = out.getDay();
    // Back up to Monday (treat Sunday as 7).
    const diff = dow === 0 ? -6 : 1 - dow;
    out.setDate(out.getDate() + diff);
    return out;
  }

  let startDate = $state('');
  let endDate = $state('');

  let dialogEl: HTMLDialogElement | null = $state(null);

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      const today = new Date();
      const monday = mondayOf(today);
      const sunday = new Date(monday);
      sunday.setDate(monday.getDate() + 6);
      startDate = isoDate(monday);
      endDate = isoDate(sunday);
      dialogEl.showModal();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    if (submitting || !startDate || !endDate) return;
    if (endDate < startDate) return;
    await onSubmit({ startDate, endDate });
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="sl-dialog">
  <form method="dialog" onsubmit={handleSubmit}>
    <h2>Generate from Meal Plan</h2>
    <p class="sl-hint">
      Pick the date range to pull ingredients from. Checked items from recipes planned in this window become your shopping list.
    </p>

    <div class="sl-row-2">
      <label>
        <span>From</span>
        <input type="date" bind:value={startDate} required />
      </label>
      <label>
        <span>To</span>
        <input type="date" bind:value={endDate} required min={startDate} />
      </label>
    </div>

    <div class="sl-actions">
      <button type="button" class="sl-btn-ghost" onclick={onClose} disabled={submitting}>
        Cancel
      </button>
      <button
        type="submit"
        class="sl-btn-primary"
        disabled={submitting || !startDate || !endDate || endDate < startDate}
      >
        {submitting ? 'Generating…' : 'Generate'}
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
  .sl-hint {
    margin: 0;
    color: var(--color-text-muted);
    font-size: 0.875rem;
    line-height: 1.5;
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
