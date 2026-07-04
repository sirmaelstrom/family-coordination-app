// ─────────────────────────────────────────────────────────────────────────
// Presence store for the de-Blazored SvelteKit shell.
//
// Replaces the Blazor circuit heartbeat (D3): every 30s it POSTs a heartbeat
// (recording the caller's current SPA path) and polls GET /api/presence/users
// for the header's online-users avatar row. `online` is a client-derived sync
// indicator — true while the last /api call succeeded, false when it failed
// (e.g. the network dropped). Same-origin cookie auth (credentials: 'include').
//
// The staleness decay (Online→Away→Offline) is driven server-side by the
// /api/presence/users handler calling PresenceService.UpdatePresence() — this
// client only reflects what the endpoint returns.
//
// ⚠ Svelte 5 rune rule: $state lives in private fields on a singleton class;
// callers read reactive values through getters (never a re-exported reassigned
// $state). Reading `presence.users` / `presence.online` in markup tracks them.
// ─────────────────────────────────────────────────────────────────────────

/** One active user in the header roster (caller excluded server-side). */
export interface PresenceUser {
  userId: number;
  displayName: string;
  initials: string | null;
  pictureUrl: string | null;
  /** Serialized lowercase by the backend's camelCase enum converter. */
  status: 'online' | 'away';
}

const HEARTBEAT_MS = 30_000;

class PresenceStore {
  #users = $state<PresenceUser[]>([]);
  #online = $state(true);
  #timer: ReturnType<typeof setInterval> | null = null;
  #started = false;

  /** The active users to show in the header (excludes the caller). */
  get users(): readonly PresenceUser[] {
    return this.#users;
  }

  /** True while the last /api/presence call succeeded; drives the sync indicator. */
  get online(): boolean {
    return this.#online;
  }

  /**
   * Begin the 30s heartbeat + roster poll. Idempotent — calling twice is a
   * no-op, so mounting from the header (the single always-present consumer) is
   * safe. Fires one tick immediately so the row/indicator populate on load.
   */
  start(): void {
    if (this.#started) return;
    this.#started = true;
    void this.#tick();
    this.#timer = setInterval(() => void this.#tick(), HEARTBEAT_MS);
  }

  /** Stop polling (header teardown). */
  stop(): void {
    if (this.#timer !== null) clearInterval(this.#timer);
    this.#timer = null;
    this.#started = false;
  }

  async #tick(): Promise<void> {
    await this.#heartbeat();
    await this.#loadUsers();
  }

  async #heartbeat(): Promise<void> {
    try {
      const res = await fetch('/api/presence/heartbeat', {
        method: 'POST',
        credentials: 'include',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({
          page: typeof window !== 'undefined' ? window.location.pathname : null,
        }),
      });
      this.#online = res.ok;
    } catch {
      this.#online = false;
    }
  }

  async #loadUsers(): Promise<void> {
    try {
      const res = await fetch('/api/presence/users', {
        credentials: 'include',
        headers: { Accept: 'application/json' },
      });
      if (!res.ok) {
        this.#online = false;
        return;
      }
      this.#online = true;
      this.#users = (await res.json()) as PresenceUser[];
    } catch {
      this.#online = false;
    }
  }
}

/** The canonical, app-wide presence singleton. Import and read reactively. */
export const presence = new PresenceStore();
