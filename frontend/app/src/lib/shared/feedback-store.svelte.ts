// ─────────────────────────────────────────────────────────────────────────
// Canonical send-feedback store — the ONE copy every launcher imports.
// Module-level singleton mirroring toast-store: one dialog instance mounted in
// +layout.svelte, opened from anywhere via openFeedback().
//
// ⚠ Svelte 5 rune rule: state lives inside a module-private $state object;
// callers read it via the accessors and mutate only through the exported
// functions (never a re-exported reassigned $state).
// ─────────────────────────────────────────────────────────────────────────

import { ApiError, apiSend } from '$lib/api/client';
import { showToast } from './toast-store.svelte';

/** Wire values for Feedback.Type (camelCase — the backend enum converter's form). */
export type FeedbackKind = 'bug' | 'featureRequest' | 'general';

const SUBMIT_URL = '/api/settings/feedback/';

/** Mirrors the Message column limit (FeedbackConfiguration); the server 400s past it. */
export const FEEDBACK_MAX_LENGTH = 4000;

const state = $state<{ open: boolean; submitting: boolean; error: string | null; submitted: number }>({
  open: false,
  submitting: false,
  error: null,
  submitted: 0,
});

/** True while the dialog should be showing (reactive — read inside markup). */
export function feedbackOpen(): boolean {
  return state.open;
}

/** True while a submit is in flight (reactive). */
export function feedbackSubmitting(): boolean {
  return state.submitting;
}

/** The last submit failure, shown inline in the dialog so the text isn't lost (reactive). */
export function feedbackError(): string | null {
  return state.error;
}

/**
 * Successful submissions this page-load (reactive). The inbox reads this to refetch on submit instead of
 * waiting out its 15s poll. Starts at 0 so an effect can guard its first run.
 */
export function feedbackSubmitCount(): number {
  return state.submitted;
}

/** Open the send-feedback dialog. Safe to call from any surface. */
export function openFeedback(): void {
  state.error = null;
  state.open = true;
}

/** Close the dialog. Refused mid-submit so a slow POST can't be abandoned into an ambiguous state. */
export function closeFeedback(): void {
  if (state.submitting) return;
  state.open = false;
  state.error = null;
}

/**
 * POST the feedback. True ⇒ stored (dialog closes, toast fires). False ⇒ rejected, and the dialog stays open
 * with the message intact and `feedbackError()` set — losing a bug report to a failed request is the outcome
 * this path exists to prevent.
 */
export async function submitFeedback(kind: FeedbackKind, message: string): Promise<boolean> {
  const trimmed = message.trim();
  if (!trimmed || state.submitting) return false;

  state.submitting = true;
  state.error = null;
  try {
    // Rides the one HTTP boundary (council round-1, PR #110): a 401 takes the
    // shared login redirect — the typed message can't be sent on a dead cookie
    // anyway, and "reload and try again" was a lie that retried forever.
    await apiSend<void>(SUBMIT_URL, 'POST', {
      type: kind,
      message: trimmed,
      currentPage: typeof window !== 'undefined' ? window.location.pathname : null,
    });

    state.open = false;
    state.submitted += 1;
    showToast({ message: 'Thanks — your feedback was sent.', kind: 'success' });
    return true;
  } catch (e) {
    state.error = describeFailure(e);
    return false;
  } finally {
    state.submitting = false;
  }
}

/**
 * Every /api 4xx carries a JSON `{ message }` (surfaced by ApiError); fall back to something
 * actionable when it's missing. 401 never reaches here (the boundary redirects to login); a 403
 * (revoked mid-session) keeps the typed text on screen — presence redirects within its poll anyway.
 */
function describeFailure(e: unknown): string {
  if (e instanceof ApiError) {
    if (e.status === 403) return "You don't have access right now — the page will redirect shortly.";
    return e.message || `Couldn't send your feedback (HTTP ${e.status}).`;
  }
  return "Couldn't send your feedback — check your connection and try again.";
}
