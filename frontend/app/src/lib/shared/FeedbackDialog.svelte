<script lang="ts">
  // Canonical send-feedback dialog — mounted ONCE in the shell +layout.svelte and
  // opened from anywhere via openFeedback() (same shape as the shared Toasts region).
  //
  // Replaces the Blazor FeedbackDialog.razor deleted by the WP-12 flip: same three
  // types, same type-driven label/placeholder, same 4000-char cap. Unlike the Blazor
  // one it does NOT write the DB itself — it POSTs /api/settings/feedback, where the
  // household/user attribution is derived server-side from the caller's cookie.
  import {
    FEEDBACK_MAX_LENGTH,
    closeFeedback,
    feedbackError,
    feedbackOpen,
    feedbackSubmitting,
    submitFeedback,
    type FeedbackKind,
  } from './feedback-store.svelte';

  const KINDS: { value: FeedbackKind; label: string; icon: string }[] = [
    { value: 'bug', label: 'Bug', icon: '🐛' },
    { value: 'featureRequest', label: 'Feature request', icon: '💡' },
    { value: 'general', label: 'General', icon: '💬' },
  ];

  // Labels ported from the deleted FeedbackDialog.razor's GetMessageLabel/Placeholder.
  const COPY: Record<FeedbackKind, { label: string; placeholder: string }> = {
    bug: {
      label: 'What went wrong?',
      placeholder: 'Describe what happened, what you expected, and steps to reproduce…',
    },
    featureRequest: {
      label: 'What would you like to see?',
      placeholder: 'Describe the feature and how it would help you…',
    },
    general: { label: 'Your feedback', placeholder: "Tell us what's on your mind…" },
  };

  let kind = $state<FeedbackKind>('general');
  let message = $state('');
  let dialogEl: HTMLDialogElement | null = $state(null);
  let textareaEl: HTMLTextAreaElement | null = $state(null);

  const open = $derived(feedbackOpen());
  const submitting = $derived(feedbackSubmitting());
  const error = $derived(feedbackError());
  const copy = $derived(COPY[kind]);
  const canSend = $derived(message.trim().length > 0 && !submitting);

  $effect(() => {
    if (!dialogEl) return;
    if (open && !dialogEl.open) {
      // Fresh form per opening — a sent report must not linger into the next one.
      kind = 'general';
      message = '';
      dialogEl.showModal();
      queueMicrotask(() => textareaEl?.focus());
    } else if (!open && dialogEl.open) {
      dialogEl.close();
    }
  });

  async function handleSubmit(e: SubmitEvent) {
    e.preventDefault();
    if (!canSend) return;
    // On failure the store keeps the dialog open and populates the inline error —
    // the typed message survives so it can be retried rather than retyped.
    await submitFeedback(kind, message);
  }

  /**
   * `<dialog>` fires `cancel` for the Esc key (a modal dialog is NOT dismissed by
   * backdrop clicks — there is no such native behaviour to intercept). Blocking it
   * mid-submit is what stops Esc bypassing closeFeedback()'s own guard.
   */
  function handleCancel(e: Event) {
    if (submitting) e.preventDefault();
  }
</script>

<dialog bind:this={dialogEl} class="sh-dialog" oncancel={handleCancel} onclose={closeFeedback}>
  <form onsubmit={handleSubmit}>
    <h2>Send feedback</h2>
    <p class="sh-sub">Report a bug, request a feature, or just tell us what you think.</p>

    <fieldset class="sh-kinds" disabled={submitting}>
      <legend>Type</legend>
      {#each KINDS as k (k.value)}
        <label class="sh-kind" class:sh-kind-on={kind === k.value}>
          <input type="radio" name="feedback-kind" value={k.value} bind:group={kind} />
          <span aria-hidden="true">{k.icon}</span>
          {k.label}
        </label>
      {/each}
    </fieldset>

    <label class="sh-field">
      <span>{copy.label}</span>
      <textarea
        bind:this={textareaEl}
        bind:value={message}
        rows="5"
        maxlength={FEEDBACK_MAX_LENGTH}
        placeholder={copy.placeholder}
        disabled={submitting}
        required
      ></textarea>
      <span class="sh-counter">{message.length} / {FEEDBACK_MAX_LENGTH}</span>
    </label>

    {#if error}
      <div class="sh-error" role="alert">{error}</div>
    {/if}

    <div class="sh-actions">
      <button type="button" class="sh-btn-ghost" onclick={closeFeedback} disabled={submitting}>
        Cancel
      </button>
      <button type="submit" class="sh-btn-primary" disabled={!canSend}>
        {submitting ? 'Sending…' : 'Send feedback'}
      </button>
    </div>
  </form>
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
    width: min(480px, calc(100vw - 32px));
    max-height: calc(100vh - 32px);
    box-shadow: var(--shadow-4);
  }
  .sh-dialog::backdrop {
    background: rgba(0, 0, 0, 0.5);
  }
  .sh-dialog form {
    display: flex;
    flex-direction: column;
    gap: 16px;
    padding: 24px;
  }
  h2 {
    margin: 0;
    font-size: 1.25rem;
    font-weight: 500;
  }
  .sh-sub {
    margin: -8px 0 0;
    color: var(--color-text-muted);
    font-size: 0.875rem;
    line-height: 1.5;
  }
  .sh-kinds {
    display: flex;
    flex-wrap: wrap;
    gap: 8px;
    border: none;
    padding: 0;
    margin: 0;
    min-width: 0;
  }
  .sh-kinds legend {
    font-size: 0.875rem;
    color: var(--color-text-muted);
    padding: 0 0 6px;
  }
  .sh-kind {
    display: inline-flex;
    align-items: center;
    gap: 6px;
    padding: 8px 14px;
    border: 1px solid var(--color-line-strong);
    border-radius: 999px;
    font-size: 0.875rem;
    cursor: pointer;
    min-height: 40px;
  }
  .sh-kind input {
    /* Visually hidden, still focusable + announced (the pill is the visible control). */
    position: absolute;
    width: 1px;
    height: 1px;
    opacity: 0;
    pointer-events: none;
  }
  .sh-kind-on {
    border-color: var(--color-primary);
    color: var(--color-primary);
    background: var(--color-action-hover);
  }
  .sh-kind:has(input:focus-visible) {
    outline: 2px solid var(--color-primary);
    outline-offset: 2px;
  }
  .sh-kinds:disabled .sh-kind {
    opacity: 0.5;
    cursor: not-allowed;
  }
  .sh-field {
    display: flex;
    flex-direction: column;
    gap: 6px;
    font-size: 0.875rem;
  }
  .sh-field > span:first-child {
    color: var(--color-text-muted);
  }
  textarea {
    font: inherit;
    color: inherit;
    padding: 10px 12px;
    border: 1px solid var(--color-line-strong);
    border-radius: var(--radius-sm);
    background: var(--color-surface);
    resize: vertical;
    min-height: 110px;
  }
  textarea:focus {
    outline: 2px solid var(--color-primary);
    outline-offset: -1px;
    border-color: var(--color-primary);
  }
  .sh-counter {
    align-self: flex-end;
    font-size: 0.75rem;
    color: var(--color-text-muted);
  }
  .sh-error {
    padding: 10px 12px;
    border-radius: var(--radius-sm);
    background: color-mix(in srgb, var(--color-error) 12%, transparent);
    color: var(--color-error);
    font-size: 0.875rem;
  }
  .sh-actions {
    display: flex;
    justify-content: flex-end;
    gap: 8px;
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
    letter-spacing: 0.02em;
  }
  .sh-btn-ghost {
    background: transparent;
    color: var(--color-primary);
  }
  .sh-btn-ghost:hover:not(:disabled) {
    background: var(--color-action-hover);
  }
  .sh-btn-primary {
    background: var(--color-primary);
    color: #fff;
    box-shadow: var(--shadow-1);
  }
  .sh-btn-primary:hover:not(:disabled) {
    background: var(--color-primary-hover);
  }
  .sh-btn-primary:disabled,
  .sh-btn-ghost:disabled {
    opacity: 0.5;
    cursor: not-allowed;
  }
</style>
