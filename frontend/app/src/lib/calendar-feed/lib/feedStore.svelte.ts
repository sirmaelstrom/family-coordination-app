// ─────────────────────────────────────────────────────────────────────────
// Calendar-feed card store (quest 17e38a88). Thin reactive shell over the pure
// transitions in feed-ops.ts. Household-scoped: the server resolves the
// household from the cookie; the store never sends an id.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`. The mutable state lives in a class instance; the instance is exported.
//
// No polling — the card is a settings surface. Every mutation awaits, then
// applies the transition the response implies; a `seq` guard drops a stale
// load that resolves after a newer one (memory fca-island-async-race-guards).
// ─────────────────────────────────────────────────────────────────────────

import type { FeedPhase } from './feed-ops';
import { copyableUrl, phaseAfterRevoke, phaseFromCreated, phaseFromStatus } from './feed-ops';
import { ApiError, createOrRotateCalendarToken, getCalendarTokenStatus, revokeCalendarToken } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

class FeedStore {
  phase = $state<FeedPhase>({ kind: 'none' });
  loading = $state(true);
  /** True once the FIRST load resolved — reloads refresh in place. */
  loaded = $state(false);
  error = $state<string | null>(null);
  /** A mutation is in flight (create/rotate/revoke) — buttons disable. */
  busy = $state(false);
  /** Which confirm dialog is open, if any. */
  confirm = $state<'rotate' | 'revoke' | null>(null);
  private seq = 0;

  get url(): string | null {
    return copyableUrl(this.phase);
  }

  async load(): Promise<void> {
    const s = ++this.seq;
    try {
      if (!this.loaded) this.loading = true;
      this.error = null;
      const status = await getCalendarTokenStatus();
      if (s !== this.seq) return;
      // Copy-once: a reload after a reveal forgets the URL on purpose.
      this.phase = phaseFromStatus(status);
      this.loaded = true;
    } catch (e) {
      if (s !== this.seq) return;
      this.error = describe(e, 'load the calendar feed status');
    } finally {
      if (s === this.seq) this.loading = false;
    }
  }

  /** Create the first link, or (after the confirm) rotate the active one. */
  async createOrRotate(): Promise<void> {
    this.busy = true;
    try {
      const created = await createOrRotateCalendarToken();
      this.phase = phaseFromCreated(created, new Date());
    } catch (e) {
      showToast({ message: describe(e, 'create the subscription link'), kind: 'error' });
      await this.load();
    } finally {
      this.busy = false;
      this.confirm = null;
    }
  }

  async revoke(): Promise<void> {
    this.busy = true;
    try {
      await revokeCalendarToken();
      this.phase = phaseAfterRevoke();
      showToast({ message: 'Subscription link revoked.', kind: 'success' });
    } catch (e) {
      showToast({ message: describe(e, 'revoke the subscription link'), kind: 'error' });
      await this.load();
    } finally {
      this.busy = false;
      this.confirm = null;
    }
  }

  /** Copy the revealed URL, with the connections-island "select manually" fallback. */
  async copyUrl(): Promise<void> {
    const url = this.url;
    if (!url) return;
    try {
      await navigator.clipboard.writeText(url);
      showToast({ message: 'Link copied to clipboard', kind: 'success' });
    } catch {
      showToast({ message: 'Unable to copy — please select the link manually', kind: 'error' });
    }
  }
}

/** The single shared calendar-feed store instance (export the instance, not the runes). */
export const feedStore = new FeedStore();
