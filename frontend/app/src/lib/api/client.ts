// ─────────────────────────────────────────────────────────────────────────
// The SPA's ONE HTTP boundary (quest 79aa83e7 — friction §A1).
//
// Every /api call goes through this module: same-origin cookie credentials,
// JSON negotiation, 204 handling, the `{ message }` error contract, and the
// 401 policy live here and nowhere else. Island `api.ts` modules keep their
// endpoint functions and request shapes, but import ApiError + the helpers
// from here (and re-export ApiError so island-internal imports are stable).
//
// 401 policy: a 401 means the cookie died — no retry can fix it, and every
// surface used to show a calm lie ("that didn't go through — refreshed").
// The default helpers bounce to the server-side login page and return a
// promise that never settles (the page is navigating; throwing would flash
// error toasts mid-redirect). 403 is NOT redirected here: it is a live
// domain signal (admin's access-denied R-C4, recipes' not-connected,
// connections' envelopes) and keeps throwing ApiError — presence remains
// the auth-revocation detector.
//
// session + presence own the app's two special auth policies (boot:
// 403 → access-denied; polling: stop-then-redirect) via `on401: 'throw'`.
// No other call site may use that knob.
// ─────────────────────────────────────────────────────────────────────────

export const LOGIN_URL = '/account/login';

/**
 * Thrown on any non-2xx response. `status` lets callers distinguish the
 * retryable concurrency conflict (409) from every other rejection.
 *
 * Since PR #90 an /api 4xx keeps its real status and always carries a JSON
 * `{ message }` (handlers write their own; the pipeline backfills a generic
 * one). Treat ANY 4xx as a non-retryable rejection EXCEPT 409, the genuine
 * xmin concurrency conflict (refetch + retry / conflict toast).
 */
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** The retryable optimistic-concurrency conflict (refetch + retry). */
  get isConflict(): boolean {
    return this.status === 409;
  }

  /**
   * A non-retryable client rejection: validation / illegal transition / not
   * found. Everything 4xx that is NOT 409.
   */
  get isClientRejection(): boolean {
    return this.status >= 400 && this.status < 500 && this.status !== 409;
  }
}

/**
 * Pull the `message` out of an /api error body. Empty body → null (callers
 * fall back to `res.statusText`); JSON with a non-empty string `message` →
 * that message; anything else (non-JSON text, JSON without a usable message)
 * → the raw text as-is.
 */
export function messageFrom(text: string): string | null {
  if (!text) return null;
  try {
    const parsed = JSON.parse(text);
    if (parsed && typeof parsed.message === 'string' && parsed.message) return parsed.message;
  } catch {
    /* not JSON — fall through to the raw text */
  }
  return text;
}

export interface ApiFetchOptions {
  /**
   * What a 401 does. 'redirect' (the default): full-page bounce to the login
   * page, promise never settles. 'throw': the caller owns auth policy — ONLY
   * session (boot: 403 → access-denied) and presence (stop polling, then
   * redirect) may use this.
   */
  on401?: 'redirect' | 'throw';
}

type Navigate = (url: string) => void;

let navigate: Navigate = (url) => {
  if (typeof window !== 'undefined') window.location.href = url;
};

/**
 * Test seam — node-env boundary tests have no window to observe a redirect on.
 * Returns a restore function; call it in afterEach so the override never leaks
 * across tests.
 */
export function setNavigateForTesting(fn: Navigate): () => void {
  const prev = navigate;
  navigate = fn;
  return () => {
    navigate = prev;
  };
}

/**
 * Where a dead-cookie 401 sends the browser: the server login page, carrying
 * the current SPA location as ReturnUrl so a re-login lands back here
 * (Login.cshtml validates it as a LOCAL url and falls back otherwise).
 */
function loginRedirectUrl(): string {
  if (typeof window === 'undefined') return LOGIN_URL;
  const here = window.location.pathname + window.location.search;
  return `${LOGIN_URL}?ReturnUrl=${encodeURIComponent(here)}`;
}

/**
 * The core request: cookie credentials, Accept + caller headers, error → ApiError
 * with the `{ message }` contract, 204 → undefined, and the 401 policy.
 */
export async function apiFetch<T>(
  url: string,
  init?: RequestInit,
  opts?: ApiFetchOptions,
): Promise<T> {
  // Normalize through Headers so every HeadersInit form (plain object, Headers
  // instance, tuple array) survives; object spread silently drops the latter two.
  const headers = new Headers(init?.headers);
  if (!headers.has('Accept')) headers.set('Accept', 'application/json');
  const res = await fetch(url, {
    // Same-origin cookie auth — the host page is already authenticated.
    ...init,
    credentials: 'include',
    headers,
  });
  if (res.status === 401 && (opts?.on401 ?? 'redirect') === 'redirect') {
    // The cookie died. Full-page redirect (NOT goto(); /account/login is a
    // root server route) — and never settle: the page is navigating away.
    navigate(loginRedirectUrl());
    return new Promise<T>(() => {});
  }
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new ApiError(res.status, messageFrom(text) ?? res.statusText);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

/** GET → parsed JSON. */
export function apiGet<T>(url: string): Promise<T> {
  return apiFetch<T>(url);
}

/**
 * A JSON (or body-less) mutation. `body` present ⇒ serialized with
 * Content-Type: application/json — including on DELETE (the house
 * version-in-body pattern); omitted ⇒ no body at all.
 */
export function apiSend<T>(url: string, method: string, body?: unknown): Promise<T> {
  return apiFetch<T>(url, {
    method,
    ...(body === undefined
      ? {}
      : { headers: { 'Content-Type': 'application/json' }, body: JSON.stringify(body) }),
  });
}

/** Multipart upload. NEVER sets Content-Type — the browser owns the boundary. */
export function apiUpload<T>(url: string, form: FormData, method = 'POST'): Promise<T> {
  return apiFetch<T>(url, { method, body: form });
}
