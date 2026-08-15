// ─────────────────────────────────────────────────────────────────────────
// Canonical send-feedback store for the SvelteKit shell — the ONE copy every
// launcher imports. Module-level singleton, mirroring toast-store: one dialog
// instance mounted in +layout.svelte, opened from anywhere via openFeedback().
//
// This is the WRITE half of the feedback surface. It was dead from the WP-12
// de-Blazor flip (which deleted FeedbackDialog.razor, the app's only feedback
// writer) until this rebuild — the admin inbox at /settings/feedback shipped
// the whole time with nothing able to reach it.
//
// ⚠ Svelte 5 rune rule: state lives inside a module-private $state object;
// callers read it via the accessors and mutate only through the exported
// functions (never a re-exported reassigned $state).
// ─────────────────────────────────────────────────────────────────────────

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
 * Count of successful submissions this page-load (reactive). A surface that RENDERS feedback — the
 * `/settings/feedback` inbox — reads this so it can refetch the moment something is submitted, instead of
 * showing a stale list until its next 15s liveness poll. Starts at 0, so an effect can guard its first run.
 */
export function feedbackSubmitCount(): number {
  return state.submitted;
}

/** Open the send-feedback dialog. Safe to call from any surface. */
export function openFeedback(): void {
  state.error = null;
  state.open = true;
}

/**
 * Close the dialog. Refused mid-submit so a slow POST can't be abandoned into an
 * ambiguous state (the dialog also disables its own controls while submitting).
 */
export function closeFeedback(): void {
  if (state.submitting) return;
  state.open = false;
  state.error = null;
}

/**
 * POST the feedback. Resolves true when it was stored (dialog closes, toast fires),
 * false when it was rejected — the dialog stays open with the message intact and
 * `feedbackError()` populated, because losing a bug report to a failed request is
 * exactly the outcome this whole path exists to prevent.
 *
 * `currentPage` is read here rather than passed in: it is the path the user was on
 * when they hit send, which is the same value presence.svelte.ts heartbeats.
 */
export async function submitFeedback(kind: FeedbackKind, message: string): Promise<boolean> {
  const trimmed = message.trim();
  if (!trimmed || state.submitting) return false;

  state.submitting = true;
  state.error = null;
  try {
    const res = await fetch(SUBMIT_URL, {
      method: 'POST',
      credentials: 'include',
      headers: { 'Content-Type': 'application/json', Accept: 'application/json' },
      body: JSON.stringify({
        type: kind,
        message: trimmed,
        currentPage: typeof window !== 'undefined' ? window.location.pathname : null,
      }),
    });

    if (!res.ok) {
      state.error = await describeFailure(res);
      return false;
    }

    state.open = false;
    state.submitted += 1;
    showToast({ message: 'Thanks — your feedback was sent.', kind: 'success' });
    return true;
  } catch {
    state.error = "Couldn't send your feedback — check your connection and try again.";
    return false;
  } finally {
    state.submitting = false;
  }
}

/**
 * Every /api 4xx here carries a JSON `{ message }` body (the app re-executes an
 * empty-body non-GET 4xx through the GET-only /not-found page, which surfaces as a
 * 405) — surface it, and fall back to something actionable if the body is missing.
 */
async function describeFailure(res: Response): Promise<string> {
  const text = await res.text().catch(() => '');
  try {
    const parsed = JSON.parse(text);
    if (parsed && typeof parsed.message === 'string' && parsed.message) return parsed.message;
  } catch {
    /* not JSON — fall through */
  }
  if (res.status === 401 || res.status === 403) return 'Your session expired — reload the page and try again.';
  return `Couldn't send your feedback (HTTP ${res.status}).`;
}
