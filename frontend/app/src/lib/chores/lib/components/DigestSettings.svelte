<script lang="ts">
  // ─────────────────────────────────────────────────────────────────────────
  // Digest-settings sheet (WP-11). A bottom sheet to configure the household's
  // weekly Discord digest: enabled toggle, send day + hour, and a WRITE-ONLY
  // webhook field.
  //
  // ⚠ MN7 (write-only webhook): the stored webhook URL is NEVER fetched,
  //   rendered, or logged here. GET returns only `hasWebhook` + `webhookHint`.
  //   The input is type="url" + autocomplete="off"; the value is sent to the
  //   server only when the user has typed something (webhookAction:'set').
  //   "Remove" sends webhookAction:'clear'; an untouched input sends 'keep'.
  //
  // ⚠ MN9 (no client date math): lastSentAt is an ISO-8601 UTC string.
  //   We render it with `new Date(isoString).toLocaleString()` — constructing a
  //   Date from a full ISO timestamp (NOT from 'YYYY-MM-DD') is safe in all
  //   timezones. We never use `new Date('YYYY-MM-DD')`.
  // ─────────────────────────────────────────────────────────────────────────
  import type { DigestDay, DigestSettingsView, DigestSettingsUpdate, WebhookAction } from '../types';
  import { getDigestSettings, updateDigestSettings, ApiError } from '../api';
  import { showToast } from '$lib/shared/toast-store.svelte';

  interface Props {
    open: boolean;
    onClose: () => void;
  }

  let { open, onClose }: Props = $props();

  // ── Form state ────────────────────────────────────────────────────────────
  let enabled = $state(false);
  let sendDayOfWeek = $state<DigestDay>('sunday');
  let sendHourLocal = $state(9);

  /**
   * Tri-state webhook control:
   *  - 'keep'  — input untouched; send webhookAction:'keep'
   *  - 'set'   — user has typed a new URL; send webhookAction:'set' + webhookUrl
   *  - 'clear' — user clicked Remove; send webhookAction:'clear'
   * We track the intent here so 'keep' is the default when the user opens the
   * sheet and does not touch the webhook field.
   */
  let webhookAction = $state<WebhookAction>('keep');
  /**
   * The new URL the user typed. ONLY sent to the server when webhookAction='set'.
   * Never logged, never rendered back, never placed in a query string (MN7).
   */
  let pendingWebhookUrl = $state('');

  /** The last-loaded view (for the status display + reset). */
  let view = $state<DigestSettingsView | null>(null);
  let loadError = $state<string | null>(null);
  let fetchInFlight = $state(false);
  let submitting = $state(false);
  let localError = $state<string | null>(null);

  let dialogEl: HTMLDialogElement | null = $state(null);

  const DAYS: { id: DigestDay; label: string }[] = [
    { id: 'sunday', label: 'Sun' },
    { id: 'monday', label: 'Mon' },
    { id: 'tuesday', label: 'Tue' },
    { id: 'wednesday', label: 'Wed' },
    { id: 'thursday', label: 'Thu' },
    { id: 'friday', label: 'Fri' },
    { id: 'saturday', label: 'Sat' },
  ];

  /** Format hour 0–23 as a friendly AM/PM label. */
  function formatHour(h: number): string {
    if (h === 0) return '12 AM (midnight)';
    if (h === 12) return '12 PM (noon)';
    return h < 12 ? `${h} AM` : `${h - 12} PM`;
  }

  function resetForm(v: DigestSettingsView): void {
    enabled = v.enabled;
    sendDayOfWeek = v.sendDayOfWeek;
    sendHourLocal = v.sendHourLocal;
    webhookAction = 'keep';
    pendingWebhookUrl = '';
    localError = null;
  }

  async function load(): Promise<void> {
    fetchInFlight = true;
    loadError = null;
    try {
      const v = await getDigestSettings();
      view = v;
      resetForm(v);
    } catch (e) {
      loadError =
        e instanceof ApiError
          ? `Couldn't load digest settings (HTTP ${e.status}).`
          : "Couldn't load digest settings right now.";
    } finally {
      fetchInFlight = false;
    }
  }

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      dialogEl.showModal();
      // Load fresh settings each time the sheet opens.
      void load();
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  /** Called when the user types in the "Replace webhook" input. */
  function onWebhookInput(e: Event): void {
    const val = (e.currentTarget as HTMLInputElement).value;
    // Updating pendingWebhookUrl; do NOT log val (MN7).
    pendingWebhookUrl = val;
    webhookAction = val.length > 0 ? 'set' : 'keep';
  }

  /** "Remove" clears the stored URL (sends webhookAction:'clear'). */
  function handleRemoveWebhook(): void {
    pendingWebhookUrl = '';
    webhookAction = 'clear';
  }

  /** Undo a pending Remove or Replace (revert to 'keep'). */
  function handleKeepWebhook(): void {
    pendingWebhookUrl = '';
    webhookAction = 'keep';
  }

  async function handleSubmit(e: SubmitEvent): Promise<void> {
    e.preventDefault();
    if (submitting) return;

    // Validate: if the user is setting a new URL it must be non-empty.
    if (webhookAction === 'set' && !pendingWebhookUrl.trim()) {
      localError = 'Paste a Discord webhook URL to save it, or leave the field empty to keep the current one.';
      return;
    }

    localError = null;
    submitting = true;

    const body: DigestSettingsUpdate = {
      enabled,
      cadence: 'weekly',
      sendDayOfWeek,
      sendHourLocal,
      webhookAction,
      // Include webhookUrl ONLY when setting — never send it on keep/clear (MN7).
      ...(webhookAction === 'set' ? { webhookUrl: pendingWebhookUrl } : {}),
    };

    try {
      const updated = await updateDigestSettings(body);
      view = updated;
      // Reset the webhook input to 'keep' after a successful save so re-opening
      // the sheet doesn't re-show the typed URL.
      resetForm(updated);
      showToast({ message: 'Digest settings saved.', kind: 'success' });
      onClose();
    } catch (e) {
      if (e instanceof ApiError && e.isClientRejection) {
        localError = "The settings couldn't be saved — check the webhook URL.";
        showToast({ message: "Digest settings couldn't be saved.", kind: 'error' });
      } else {
        showToast({ message: 'Something went wrong saving digest settings.', kind: 'error' });
      }
    } finally {
      submitting = false;
    }
  }

  /**
   * Render a lastSentAt ISO-8601 UTC string as a human-friendly local time.
   * We construct Date from the full ISO timestamp (includes time + 'Z') —
   * this is MN9-safe. NEVER use `new Date('YYYY-MM-DD')`.
   */
  function formatLastSent(iso: string): string {
    // Full ISO-8601 UTC string (e.g. "2026-05-30T09:00:00Z") is safe to parse
    // because it includes the time component and the 'Z' offset — no ambiguous
    // date-only string involved (MN9 safe).
    return new Date(iso).toLocaleString();
  }
</script>

<dialog bind:this={dialogEl} onclose={onClose} class="ch-sheet">
  <form method="dialog" onsubmit={handleSubmit}>
    <header class="ch-sheet-head">
      <h2>Weekly Digest</h2>
    </header>

    <div class="ch-sheet-body">
      {#if fetchInFlight}
        <p class="ch-hint">Loading settings…</p>
      {:else if loadError}
        <p class="ch-sheet-error" role="alert">{loadError}</p>
        <button type="button" class="ch-btn-ghost" onclick={load}>Retry</button>
      {:else if view}
        <!-- Enabled toggle -->
        <label class="ch-field ch-toggle-field">
          <span class="ch-field-label">Send weekly digest</span>
          <input
            type="checkbox"
            role="switch"
            bind:checked={enabled}
            aria-label="Enable weekly digest"
          />
        </label>

        <!-- Send day -->
        <fieldset class="ch-field" disabled={!enabled}>
          <legend class="ch-field-label">Send day</legend>
          <div class="ch-chip-row" role="group" aria-label="Send day of week">
            {#each DAYS as day (day.id)}
              <button
                type="button"
                class="ch-chip"
                class:active={sendDayOfWeek === day.id}
                aria-pressed={sendDayOfWeek === day.id}
                onclick={() => (sendDayOfWeek = day.id)}
              >
                {day.label}
              </button>
            {/each}
          </div>
        </fieldset>

        <!-- Send hour -->
        <fieldset class="ch-field" disabled={!enabled}>
          <legend class="ch-field-label">Send time (local)</legend>
          <div class="ch-hour-row">
            <input
              type="range"
              min="0"
              max="23"
              step="1"
              bind:value={sendHourLocal}
              aria-label="Send hour"
              class="ch-hour-slider"
            />
            <span class="ch-hour-label">{formatHour(sendHourLocal)}</span>
          </div>
        </fieldset>

        <!-- Webhook (WRITE-ONLY — MN7) -->
        <fieldset class="ch-field">
          <legend class="ch-field-label">Discord webhook</legend>

          <!-- Status display: show hasWebhook + hint. NEVER show the actual URL. -->
          <p class="ch-hint ch-webhook-status">
            {#if webhookAction === 'clear'}
              <span class="ch-webhook-pending-clear">Will be removed on save.</span>
            {:else if view.hasWebhook && webhookAction === 'keep'}
              Webhook set ••••{view.webhookHint ?? ''}
            {:else if !view.hasWebhook && webhookAction === 'keep'}
              Not configured
            {:else if webhookAction === 'set'}
              New webhook will be saved.
            {/if}
          </p>

          {#if webhookAction === 'clear'}
            <!-- Pending removal — offer an undo -->
            <button type="button" class="ch-btn-ghost ch-webhook-undo" onclick={handleKeepWebhook}>
              Undo remove
            </button>
          {:else if webhookAction === 'set'}
            <!-- URL typed — show the input + offer to cancel the replacement -->
            <input
              type="url"
              autocomplete="off"
              placeholder="https://discord.com/api/webhooks/…"
              class="ch-webhook-input"
              aria-label="New Discord webhook URL"
              value={pendingWebhookUrl}
              oninput={onWebhookInput}
            />
            <button type="button" class="ch-btn-ghost ch-webhook-undo" onclick={handleKeepWebhook}>
              Cancel replacement
            </button>
          {:else}
            <!-- 'keep' state — show the replace input + optional Remove -->
            <input
              type="url"
              autocomplete="off"
              placeholder={view.hasWebhook ? 'Paste a new URL to replace it' : 'Paste Discord webhook URL'}
              class="ch-webhook-input"
              aria-label="Replace Discord webhook URL"
              value={pendingWebhookUrl}
              oninput={onWebhookInput}
            />
            {#if view.hasWebhook}
              <button
                type="button"
                class="ch-btn-ghost ch-webhook-remove"
                onclick={handleRemoveWebhook}
              >
                Remove webhook
              </button>
            {/if}
          {/if}
        </fieldset>

        <!-- Last sent -->
        {#if view.lastSentAt}
          <p class="ch-hint ch-last-sent">
            Last sent: {formatLastSent(view.lastSentAt)}
          </p>
        {/if}

        {#if localError}
          <p class="ch-sheet-error" role="alert">{localError}</p>
        {/if}
      {/if}
    </div>

    <footer class="ch-sheet-actions">
      <button type="button" class="ch-btn-ghost" onclick={onClose} disabled={submitting}>
        Cancel
      </button>
      <button
        type="submit"
        class="ch-btn-primary"
        disabled={submitting || fetchInFlight || !!loadError}
      >
        {submitting ? 'Saving…' : 'Save settings'}
      </button>
    </footer>
  </form>
</dialog>

<style>
  .ch-sheet {
    border: none;
    padding: 0;
    background: var(--color-surface);
    color: var(--color-text);
    box-shadow: var(--shadow-4);
    width: min(520px, 100vw);
    max-height: 90vh;
    margin: auto auto 0;
    border-radius: var(--radius-md) var(--radius-md) 0 0;
  }
  @media (min-width: 600px) {
    .ch-sheet {
      margin: auto;
      border-radius: var(--radius-md);
    }
  }
  .ch-sheet::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .ch-sheet form {
    display: flex;
    flex-direction: column;
    max-height: 90vh;
  }
  .ch-sheet-head {
    padding: 20px 24px 8px;
  }
  .ch-sheet-head h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .ch-sheet-body {
    overflow-y: auto;
    padding: 8px 24px 16px;
    display: flex;
    flex-direction: column;
    gap: 18px;
  }

  .ch-field {
    border: none;
    margin: 0;
    padding: 0;
    display: flex;
    flex-direction: column;
    gap: 8px;
  }
  .ch-field[disabled] {
    opacity: 0.45;
    pointer-events: none;
  }
  .ch-field-label {
    font-size: 0.875rem;
    font-weight: 500;
    color: var(--color-text-muted);
    padding: 0;
  }

  /* Toggle row */
  .ch-toggle-field {
    flex-direction: row;
    align-items: center;
    justify-content: space-between;
  }
  .ch-toggle-field input[type='checkbox'] {
    width: 44px;
    height: 24px;
    cursor: pointer;
    accent-color: var(--color-primary);
  }

  /* Day chips */
  .ch-chip-row {
    display: flex;
    gap: 6px;
    flex-wrap: wrap;
  }
  .ch-chip {
    font: inherit;
    font-size: 0.8125rem;
    font-weight: 500;
    border: 1px solid var(--color-line-strong);
    background: transparent;
    color: var(--color-text-muted);
    padding: 6px 10px;
    min-height: 36px;
    border-radius: 18px;
    cursor: pointer;
    transition:
      background-color 0.15s,
      color 0.15s,
      border-color 0.15s;
  }
  .ch-chip.active {
    background: var(--color-primary);
    border-color: var(--color-primary);
    color: #fff;
  }
  .ch-chip:hover:not(.active) {
    background: var(--color-action-hover);
    color: var(--color-text);
  }

  /* Hour slider */
  .ch-hour-row {
    display: flex;
    align-items: center;
    gap: 12px;
  }
  .ch-hour-slider {
    flex: 1;
    accent-color: var(--color-primary);
    cursor: pointer;
  }
  .ch-hour-label {
    font-size: 0.875rem;
    color: var(--color-text);
    min-width: 108px;
    white-space: nowrap;
  }

  /* Webhook */
  .ch-webhook-status {
    margin-bottom: 4px;
  }
  .ch-webhook-pending-clear {
    color: var(--color-error);
    font-style: italic;
  }
  .ch-webhook-input {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    min-height: 44px;
    width: 100%;
    box-sizing: border-box;
  }
  .ch-webhook-input:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .ch-webhook-remove {
    align-self: flex-start;
    color: var(--color-error);
    font-size: 0.875rem;
    padding: 4px 0;
  }
  .ch-webhook-remove:hover {
    background: transparent;
    text-decoration: underline;
  }
  .ch-webhook-undo {
    align-self: flex-start;
    font-size: 0.875rem;
    padding: 4px 0;
  }

  .ch-last-sent {
    font-size: 0.8125rem;
    color: var(--color-text-muted);
    margin: 0;
  }

  .ch-hint {
    margin: 0;
    font-size: 0.8125rem;
    color: var(--color-text-muted);
  }
  .ch-sheet-error {
    margin: 0;
    font-size: 0.875rem;
    color: var(--color-error);
  }

  .ch-sheet-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
    padding: 16px 24px calc(20px + env(safe-area-inset-bottom, 0px));
    border-top: 1px solid var(--color-line);
  }
  .ch-btn-ghost,
  .ch-btn-primary {
    font: inherit;
    padding: 10px 20px;
    border-radius: var(--radius-sm);
    border: none;
    cursor: pointer;
    min-height: 44px;
    font-weight: 500;
    letter-spacing: 0.02em;
  }
  .ch-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .ch-btn-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .ch-btn-primary {
    background: var(--color-primary);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .ch-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .ch-btn-primary:disabled,
  .ch-btn-ghost:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
