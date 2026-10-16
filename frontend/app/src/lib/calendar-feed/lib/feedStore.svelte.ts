// ─────────────────────────────────────────────────────────────────────────
// Calendar-feed card store (quest 17e38a88). Thin reactive shell over the pure
// transitions in feed-ops.ts. Household-scoped: the server resolves the
// household from the cookie; the store never sends an id.
//
// ⚠ Svelte 5 rune rule (global CORRECTION): never `export` a reassigned
// `$state`. The mutable state lives in a class instance; the instance is exported.
//
// No polling — the card is a settings surface. Ordering lives in a Resource
// (quest 41337b6f): a status load that overlaps a create/rotate/revoke never
// commits, and one confirming status load follows every write.
//
// COPY-ONCE, kept apart from the status (spec §5 row 4): the revealed URL is its
// own state, set only by a create/rotate response. A status load never produces
// one, and it does not erase one either while the status still says "active" —
// the confirming load a write owes at settle must not throw away the URL that
// write's own response just revealed. The URL is forgotten by a page (re)load
// (`load()`), a revoke, a failed create/rotate, or a status saying no token is
// active. Accepted limit: the status cannot say WHICH token is active, so a
// rotation from another device leaves this card's (now dead) URL up until the
// next page load — the card does not poll. A failed revoke forgets the URL too
// (as before): a network failure can't prove the DELETE didn't land.
// ─────────────────────────────────────────────────────────────────────────

import type { FeedPhase } from './feed-ops';
import { copyableUrl, phaseAfterRevoke, phaseFromCreated, phaseFromStatus } from './feed-ops';
import type { CalendarTokenStatusDto } from './types';
import { ApiError, createOrRotateCalendarToken, getCalendarTokenStatus, revokeCalendarToken } from './api';
import { Resource } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

type RevealedPhase = Extract<FeedPhase, { kind: 'revealed' }>;

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

class FeedStore {
  /** What the last status load said: `none` or `active`, never a URL. */
  #status = $state<FeedPhase>({ kind: 'none' });
  /** The copy-once reveal from a create/rotate response, or null. */
  #reveal = $state<RevealedPhase | null>(null);
  /** The card's phase: the reveal while one is held, else the status. */
  phase = $derived<FeedPhase>(this.#reveal ?? this.#status);
  loading = $state(true);
  /** True once the FIRST load resolved — reloads refresh in place. */
  loaded = $state(false);
  error = $state<string | null>(null);
  /** A mutation is in flight (create/rotate/revoke) — buttons disable. */
  busy = $state(false);
  /** Which confirm dialog is open, if any. */
  confirm = $state<'rotate' | 'revoke' | null>(null);

  #resource = new Resource<CalendarTokenStatusDto>({
    fetch: () => getCalendarTokenStatus(),
    commit: (status) => {
      this.#status = phaseFromStatus(status);
      // No active token ⇒ no URL can still work (revoked elsewhere). An active status keeps the reveal:
      // the GET cannot say WHICH token is active, and it may be the write's own confirming load.
      if (!status.active) this.#reveal = null;
      this.loaded = true;
    },
    onLoadStart: () => {
      if (!this.loaded) this.loading = true;
      this.error = null;
    },
    onLoadError: (e) => {
      this.error = describe(e, 'load the calendar feed status');
    },
    onLoadIdle: () => {
      this.loading = false;
    },
  });

  get url(): string | null {
    return copyableUrl(this.phase);
  }

  /** A page (re)load: copy-once, so it forgets a revealed URL, then loads the status. */
  async load(): Promise<void> {
    this.#reveal = null;
    await this.#resource.load();
  }

  /** Create the first link, or (after the confirm) rotate the active one. */
  async createOrRotate(): Promise<void> {
    this.busy = true;
    try {
      // reload 'always' (the run default): a confirming status load follows the write, and a failure
      // reconciles to server truth (it reloaded on any error before, too).
      await this.#resource.run({
        call: () => createOrRotateCalendarToken(),
        commit: (created) => {
          this.#reveal = phaseFromCreated(created, new Date()) as RevealedPhase;
          this.#status = { kind: 'active', createdAt: this.#reveal.createdAt };
        },
      });
    } catch (e) {
      // A failed rotate may have killed the URL on screen: forget it (the old reload did too).
      this.#reveal = null;
      showToast({ message: describe(e, 'create the subscription link'), kind: 'error' });
    } finally {
      this.busy = false;
      this.confirm = null;
    }
  }

  async revoke(): Promise<void> {
    this.busy = true;
    try {
      await this.#resource.run({
        call: () => revokeCalendarToken(),
        commit: () => {
          this.#reveal = null;
          this.#status = phaseAfterRevoke();
        },
      });
      showToast({ message: 'Subscription link revoked.', kind: 'success' });
    } catch (e) {
      this.#reveal = null;
      showToast({ message: describe(e, 'revoke the subscription link'), kind: 'error' });
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
