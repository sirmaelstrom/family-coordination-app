// ─────────────────────────────────────────────────────────────────────────
// Canonical session store for the de-Blazored SvelteKit shell.
//
// Fetches GET /api/me ONCE (same-origin cookie auth, credentials: 'include')
// and exposes the authenticated caller's household + user identity. Every route
// reads identity from THIS store — route WPs must NOT each re-fetch /api/me
// (orchestrator M8: avoids duplicate fetches + divergent 401 handling).
//
// On 401 the store triggers a FULL-PAGE redirect to /account/login (the still
// server-side login) — NOT a SvelteKit navigation, and NOT base-prefixed, since
// /account/login is a root server route, not an /app SPA route.
//
// ⚠ Svelte 5 rune rule: state lives in $state-backed private fields on a
// singleton class; callers read reactive values through getters (never a
// re-exported reassigned $state). Reading `session.user` / `session.ready` etc.
// inside component markup or $derived tracks them reactively.
// ─────────────────────────────────────────────────────────────────────────

/** The identity payload returned by GET /api/me (see MeEndpoints.MeDto). */
export interface SessionUser {
  householdId: number;
  userId: number;
  userName: string;
  initials: string | null;
  pictureUrl: string | null;
  isSiteAdmin: boolean;
}

/** Lifecycle of the one-shot /api/me load. */
export type SessionStatus = 'idle' | 'loading' | 'ready' | 'error';

/** Raw JSON shape of GET /api/me (camelCased MeDto). */
interface MeResponse {
  householdId: number;
  userId: number;
  userName: string;
  initials: string | null;
  pictureUrl: string | null;
  isSiteAdmin: boolean;
}

const LOGIN_URL = '/account/login';
const ACCESS_DENIED_URL = '/account/access-denied';

class SessionStore {
  #user = $state<SessionUser | null>(null);
  #status = $state<SessionStatus>('idle');
  #error = $state<string | null>(null);
  #loadPromise: Promise<void> | null = null;

  /** The authenticated user, or null until `ready`. */
  get user(): SessionUser | null {
    return this.#user;
  }

  get status(): SessionStatus {
    return this.#status;
  }

  get error(): string | null {
    return this.#error;
  }

  /** True once /api/me has resolved successfully. Gate ctx()/route bodies on this. */
  get ready(): boolean {
    return this.#status === 'ready';
  }

  get loading(): boolean {
    return this.#status === 'loading';
  }

  // ── Convenience identity getters (null/false until ready) ────────────────
  get householdId(): number | null {
    return this.#user?.householdId ?? null;
  }
  get userId(): number | null {
    return this.#user?.userId ?? null;
  }
  get userName(): string | null {
    return this.#user?.userName ?? null;
  }
  get initials(): string | null {
    return this.#user?.initials ?? null;
  }
  get pictureUrl(): string | null {
    return this.#user?.pictureUrl ?? null;
  }
  get isSiteAdmin(): boolean {
    return this.#user?.isSiteAdmin ?? false;
  }

  /**
   * Load /api/me exactly once. Subsequent calls return the same in-flight /
   * settled promise, so mounting the store from multiple components (layout +
   * a route) never double-fetches. Call from the root layout's onMount.
   */
  load(): Promise<void> {
    if (this.#loadPromise) return this.#loadPromise;
    this.#loadPromise = this.#doLoad();
    return this.#loadPromise;
  }

  async #doLoad(): Promise<void> {
    this.#status = 'loading';
    this.#error = null;
    try {
      const res = await fetch('/api/me', {
        credentials: 'include',
        headers: { Accept: 'application/json' },
      });

      if (res.status === 401) {
        // No session on this origin → bounce to the server-side login.
        // Full-page redirect (NOT goto()); NOT base-prefixed (root server route).
        if (typeof window !== 'undefined') {
          window.location.href = LOGIN_URL;
        }
        return;
      }

      if (res.status === 403) {
        // Authenticated but not authorized (e.g. removed from the whitelist) —
        // an error screen would dead-end them; the server access-denied page
        // routes them to request access / sign in as someone else.
        if (typeof window !== 'undefined') {
          window.location.href = ACCESS_DENIED_URL;
        }
        return;
      }

      if (!res.ok) throw new Error(`/api/me returned ${res.status}`);

      const dto = (await res.json()) as MeResponse;
      this.#user = {
        householdId: dto.householdId,
        userId: dto.userId,
        userName: dto.userName,
        initials: dto.initials ?? null,
        pictureUrl: dto.pictureUrl ?? null,
        isSiteAdmin: dto.isSiteAdmin,
      };
      this.#status = 'ready';
    } catch (e) {
      this.#error = e instanceof Error ? e.message : String(e);
      this.#status = 'error';
    }
  }
}

/** The canonical, app-wide session singleton. Import and read reactively. */
export const session = new SessionStore();

// ─────────────────────────────────────────────────────────────────────────
// ShellContext + ctx() helper
//
// Every island route renders `<App {ctx} />`. ctx() composes the ShellContext
// identically everywhere: identity from the session store + the route's own
// params (listId / recipeId / …). Extend RouteParams as later routes need new
// ids — the fields are additive/optional, so adding one never breaks callers.
// ─────────────────────────────────────────────────────────────────────────

/** Route-specific ids a page passes to ctx(). All optional + additive. */
export interface RouteParams {
  listId?: number | null;
  recipeId?: number | null;
}

/** The context object handed to every island `<App {ctx} />`. */
export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
  initials: string | null;
  pictureUrl: string | null;
  isSiteAdmin: boolean;
  listId: number | null;
  recipeId: number | null;
}

/**
 * Build a ShellContext from the canonical session + this route's params.
 * MUST be called only when `session.ready` (routes gate their body on it),
 * otherwise it throws — identity is never guessed client-side.
 */
export function ctx(params: RouteParams = {}): ShellContext {
  const u = session.user;
  if (!u) {
    throw new Error('ctx() called before session is ready — gate the route body on `session.ready`.');
  }
  return {
    householdId: u.householdId,
    userId: u.userId,
    userName: u.userName,
    initials: u.initials,
    pictureUrl: u.pictureUrl,
    isSiteAdmin: u.isSiteAdmin,
    listId: params.listId ?? null,
    recipeId: params.recipeId ?? null,
  };
}
