// ─────────────────────────────────────────────────────────────────────────
// Calendar-feed card store (quest 17e38a88). Thin reactive shell over the pure
// transitions in feed-ops.ts. Caller-scoped: the server resolves the member and
// household from the cookie; the store never sends an id. Each member has their own
// link (quest 8dba87f4), so the status, rotate and revoke are about the caller's link.
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
import type { CalendarTokenCreatedDto, CalendarTokenStatusDto } from './types';
import {
  ApiError,
  createOrRotateCalendarToken,
  createOrRotateChoresCalendarToken,
  getCalendarTokenStatus,
  getChoresCalendarTokenStatus,
  revokeCalendarToken,
  revokeChoresCalendarToken,
  revokeSharedCalendarToken,
} from './api';
import { Resource } from '$lib/data/resource.svelte';
import { showToast } from '$lib/shared/toast-store.svelte';

type RevealedPhase = Extract<FeedPhase, { kind: 'revealed' }>;

/**
 * The four calls one feed's card makes (quest 3bdbcab7: the meal feed and the chores feed share the store).
 * `revokeShared` exists only for the meal feed, the one feed that ever had a household-shared link.
 */
export interface FeedApi {
  getStatus(): Promise<CalendarTokenStatusDto>;
  createOrRotate(): Promise<CalendarTokenCreatedDto>;
  revoke(): Promise<void>;
  revokeShared?: () => Promise<void>;
}

function describe(e: unknown, action: string): string {
  if (e instanceof ApiError) return e.message || `Couldn't ${action} (HTTP ${e.status}).`;
  if (e instanceof Error) return e.message;
  return `Couldn't ${action}.`;
}

export class FeedStore {
  readonly #api: FeedApi;

  constructor(api: FeedApi) {
    this.#api = api;
  }

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
  confirm = $state<'rotate' | 'revoke' | 'revoke-shared' | null>(null);
  /**
   * The legacy household-shared link (from before per-member links, quest 8dba87f4): whether
   * one is still active. Read-only here except for its revoke; it is never revealed or rotated.
   */
  shared = $state<{ active: boolean; createdAt: string | null }>({ active: false, createdAt: null });

  #resource = new Resource<CalendarTokenStatusDto>({
    fetch: () => this.#api.getStatus(),
    commit: (status) => {
      this.shared = { active: status.sharedLinkActive === true, createdAt: status.sharedLinkCreatedAt ?? null };
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
        call: () => this.#api.createOrRotate(),
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
        call: () => this.#api.revoke(),
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

  /** Revoke the legacy household-shared link. The caller's own link (and its reveal) is untouched. */
  async revokeShared(): Promise<void> {
    const revokeShared = this.#api.revokeShared;
    if (!revokeShared) return; // a feed with no shared link (chores) has nothing to revoke
    this.busy = true;
    try {
      await this.#resource.run({
        call: () => revokeShared(),
        commit: () => {
          this.shared = { active: false, createdAt: null };
        },
      });
      showToast({ message: 'Shared household link revoked.', kind: 'success' });
    } catch (e) {
      showToast({ message: describe(e, 'revoke the shared household link'), kind: 'error' });
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

// The calls are wrapped, not passed by reference, so a test's module mock of './api' is what runs.

/** The meal-plan feed card's store (export the instance, not the runes). */
export const feedStore = new FeedStore({
  getStatus: () => getCalendarTokenStatus(),
  createOrRotate: () => createOrRotateCalendarToken(),
  revoke: () => revokeCalendarToken(),
  revokeShared: () => revokeSharedCalendarToken(),
});

/** The chores feed card's store (quest 3bdbcab7): the caller's own chores link. No shared link. */
export const choresFeedStore = new FeedStore({
  getStatus: () => getChoresCalendarTokenStatus(),
  createOrRotate: () => createOrRotateChoresCalendarToken(),
  revoke: () => revokeChoresCalendarToken(),
});
