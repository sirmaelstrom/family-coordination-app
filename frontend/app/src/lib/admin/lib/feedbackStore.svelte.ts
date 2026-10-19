// ─────────────────────────────────────────────────────────────────────────
// Feedback view store (#admin-feedback-root). Source of truth for the feedback
// list + the caller's isSiteAdmin flag. DUAL-MODE: the server scopes the list
// (admin → all households; regular → own household, R-C1), so the store just
// renders whatever it's handed and uses `isSiteAdmin` for affordances/copy.
//
// Discipline: onMount one-time load; this view POLLS at 15s. Load and write
// ordering live in a Resource (quest 41337b6f): a poll that overlaps a
// mark-read/resolve/reopen never commits (one in flight is retired, one asked
// for mid-write is deferred), and the list reloads once when the last write
// settles, success or failure. The IDOR scope (a non-admin's mutation of
// another household's item ⇒ 404) is enforced SERVER-side; here a 404 just
// reconciles + toasts.
// ─────────────────────────────────────────────────────────────────────────

import type { FeedbackDto, FeedbackListDto } from './types';
import {
  ApiError,
  getFeedback,
  markFeedbackRead,
  markFeedbackResolved,
  reopenFeedback,
} from './api';
import { Resource } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

class FeedbackStore {
  items = $state<FeedbackDto[]>([]);
  isSiteAdmin = $state(false);
  loading = $state(true);
  error = $state<string | null>(null);

  private hasLoaded = false;

  #resource = new Resource<FeedbackListDto>({
    fetch: () => getFeedback(),
    commit: (dto) => {
      this.items = dto.items;
      this.isSiteAdmin = dto.isSiteAdmin;
      this.hasLoaded = true;
    },
    onLoadStart: () => {
      if (!this.hasLoaded) this.loading = true; // spinner only on the FIRST load
      this.error = null;
    },
    onLoadError: (e) => {
      this.error = describe(e, 'load feedback');
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  async load(): Promise<void> {
    await this.#resource.load();
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

  /** A write-then-reload action (reload 'always'); the success toast sits outside the write window. */
  private async mutate(action: () => Promise<void>, label: string, successMessage?: string): Promise<void> {
    try {
      await this.#resource.run({ call: action });
    } catch (e) {
      showToast({ message: describe(e, label), kind: 'error' });
      return;
    }
    if (successMessage) showToast({ message: successMessage, kind: 'success' });
  }
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

export const feedbackStore = new FeedbackStore();
