import type { CalendarTokenStatusDto, CalendarTokenCreatedDto } from './types';
// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). Calendar-feed semantics: every 4xx is a non-retryable
// rejection → reconcile (refetch status) + a calm toast.
import { ApiError, apiGet, apiSend } from '$lib/api/client';

export { ApiError };

const BASE = '/api/meal-plan/calendar-token';

/** #1 Whether the caller has an active feed link (and whether the legacy shared one is), and since when. Never the token. */
export async function getCalendarTokenStatus(): Promise<CalendarTokenStatusDto> {
  return apiGet<CalendarTokenStatusDto>(BASE);
}

/**
 * #2 Create a token, or rotate the active one (create + revoke in one server
 * transaction). Returns the plaintext token and the absolute feed URL — the
 * only response that ever carries them.
 */
export async function createOrRotateCalendarToken(): Promise<CalendarTokenCreatedDto> {
  return apiSend<CalendarTokenCreatedDto>(BASE, 'POST');
}

/** #3 Revoke the caller's own link → 204. That URL stops answering (404, no oracle); other members' links keep working. */
export async function revokeCalendarToken(): Promise<void> {
  await apiSend<void>(BASE, 'DELETE');
}

/** #4 Revoke the legacy household-shared link → 204. Members' own links keep working. */
export async function revokeSharedCalendarToken(): Promise<void> {
  await apiSend<void>(`${BASE}/shared`, 'DELETE');
}

// ── The caller's chores feed (quest 3bdbcab7): the same contract, Feed = Chores, no shared link ──
const CHORES_BASE = '/api/chores/calendar-token';

/** Whether the caller has an active chores feed link, and since when. Never the token. */
export async function getChoresCalendarTokenStatus(): Promise<CalendarTokenStatusDto> {
  return apiGet<CalendarTokenStatusDto>(CHORES_BASE);
}

/** Create or rotate the caller's chores link → { token, url } (the only response carrying them). */
export async function createOrRotateChoresCalendarToken(): Promise<CalendarTokenCreatedDto> {
  return apiSend<CalendarTokenCreatedDto>(CHORES_BASE, 'POST');
}

/** Revoke the caller's chores link → 204. Their meal link is another feed and keeps working. */
export async function revokeChoresCalendarToken(): Promise<void> {
  await apiSend<void>(CHORES_BASE, 'DELETE');
}
