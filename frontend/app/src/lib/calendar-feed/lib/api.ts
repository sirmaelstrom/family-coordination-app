import type { CalendarTokenStatusDto, CalendarTokenCreatedDto } from './types';
// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). Calendar-feed semantics: every 4xx is a non-retryable
// rejection → reconcile (refetch status) + a calm toast.
import { ApiError, apiGet, apiSend } from '$lib/api/client';

export { ApiError };

const BASE = '/api/meal-plan/calendar-token';

/** #1 Whether the household has an active feed token, and since when. Never the token. */
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

/** #3 Revoke the active token → 204. The feed URL stops answering (404, no oracle). */
export async function revokeCalendarToken(): Promise<void> {
  await apiSend<void>(BASE, 'DELETE');
}
