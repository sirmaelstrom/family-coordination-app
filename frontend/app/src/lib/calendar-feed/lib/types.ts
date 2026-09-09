// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the /api/meal-plan/calendar-token contract. Source of truth:
//   - Endpoints/CalendarTokenEndpoints.cs (anonymous-object responses)
//   - tests/.../Integration/CalendarTokenEndpointTests.cs
//   - data/outputs/workshops/fca-ical-export/spec-lite.md (token semantics)
//
// ⚠ COPY-ONCE: the plaintext token (and therefore the feed URL) is returned by
//   POST only. GET deliberately omits it — the server stores SHA-256(token) and
//   cannot show it again. A UI that needs the URL after the POST response is
//   gone must rotate (POST again), which kills the previous URL.
// ⚠ DATES: `createdAt` is a FULL ISO-8601 instant (UTC) or null — render local
//   via new Date(iso) (NEVER new Date('YYYY-MM-DD')).
// ─────────────────────────────────────────────────────────────────────────

import type { ShellContext as AppShellContext } from '$lib/session.svelte';

export type ShellContext = AppShellContext;

/** GET /api/meal-plan/calendar-token → { active, createdAt } — never the token. */
export interface CalendarTokenStatusDto {
  active: boolean;
  /** full ISO-8601 instant (UTC) when a token is active, else null/undefined. */
  createdAt?: string | null;
}

/** POST /api/meal-plan/calendar-token → { token, url } — the ONLY time the URL is readable. */
export interface CalendarTokenCreatedDto {
  token: string;
  /** Absolute feed URL built from the request's scheme + host, token in the query. */
  url: string;
}
