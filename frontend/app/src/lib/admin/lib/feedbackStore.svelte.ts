// ─────────────────────────────────────────────────────────────────────────
// Feedback view store (#admin-feedback-root). Source of truth for the feedback
// list + the caller's isSiteAdmin flag. DUAL-MODE: the server scopes the list
// (admin → all households; regular → own household, R-C1), so the store just
// renders whatever it's handed and uses `isSiteAdmin` for affordances/copy.
//
// Discipline: onMount one-time load, `loadSeq` guard on every load (this view
// POLLS at 15s), await-then-reload mutations, reconcile on any 4xx. The IDOR
// scope (a non-admin's mutation of another household's item ⇒ 404) is enforced
// SERVER-side; here a 404 just reconciles + toasts.
// ─────────────────────────────────────────────────────────────────────────

import type { FeedbackDto } from './types';
import {
  ApiError,
  getFeedback,
  markFeedbackRead,
  markFeedbackResolved,
  reopenFeedback,
} from './api';
import { showToast } from '$lib/shared/toast-store.svelte';

class FeedbackStore {
  items = $state<FeedbackDto[]>([]);
  isSiteAdmin = $state(false);
  loading = $state(true);
  error = $state<string | null>(null);

  private hasLoaded = false;
  private seq = 0;

  async load(): Promise<void> {
    const s = ++this.seq;
    try {
      if (!this.hasLoaded) this.loading = true;
      this.error = null;
      const dto = await getFeedback();
      if (s !== this.seq) return; // stale poll — drop
      this.items = dto.items;
      this.isSiteAdmin = dto.isSiteAdmin;
      this.hasLoaded = true;
    } catch (e) {
      if (s !== this.seq) return;
      this.error = describe(e, 'load feedback');
    } finally {
      if (s === this.seq) this.loading = false;
    }
  }

  async markRead(id: number): Promise<void> {
    await this.mutate(() => markFeedbackRead(id), 'mark read');
  }

  async markResolved(id: number): Promise<void> {
    await this.mutate(() => markFeedbackResolved(id), 'resolve', 'Marked as resolved.');
  }

  async reopen(id: number): Promise<void> {
    await this.mutate(() => reopenFeedback(id), 'reopen');
  }

  private async mutate(action: () => Promise<void>, label: string, successMessage?: string): Promise<void> {
    try {
      await action();
      await this.load();
      if (successMessage) showToast({ message: successMessage, kind: 'success' });
    } catch (e) {
      if (e instanceof ApiError) await this.load();
      showToast({ message: describe(e, label), kind: 'error' });
    }
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

export const feedbackStore = new FeedbackStore();
