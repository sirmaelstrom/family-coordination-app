import type { HouseholdRequestsDto, HouseholdSummaryDto, FeedbackListDto } from './types';

const REQUESTS = '/api/settings/household-requests';
const HOUSEHOLDS = '/api/settings/households';
const FEEDBACK = '/api/settings/feedback';

// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). Admin semantics: any 4xx is a non-retryable rejection →
// reconcile + calm toast. The 403 on the households GET is special-cased by
// the store as the access-denied signal (R-C4 — the 403 IS the signal; no
// /context endpoint), which is exactly why the client boundary never
// redirects on 403.
import { ApiError, apiGet, apiSend } from '$lib/api/client';

export { ApiError };

// ─── Household requests (site-admin only) ───────────────────────────────────

/** #1 Requests (pending-first) + existing households. 403 (ApiError) for a non-admin → access denied. */
export async function getHouseholdRequests(): Promise<HouseholdRequestsDto> {
  return apiGet<HouseholdRequestsDto>(`${REQUESTS}/`);
}

/** #2 Approve → the new household summary (201). Already-reviewed ⇒ 409 (ApiError); unknown ⇒ 404. */
export async function approveRequest(id: number): Promise<HouseholdSummaryDto> {
  return apiSend<HouseholdSummaryDto>(`${REQUESTS}/${id}/approve`, 'POST');
}

/** #3 Reject with an OPTIONAL reason → 204. Already-reviewed ⇒ 409 (ApiError); unknown ⇒ 404. */
export async function rejectRequest(id: number, reason: string): Promise<void> {
  await apiSend<void>(`${REQUESTS}/${id}/reject`, 'POST', { reason });
}

/**
 * Admin-initiated household create (the "push" invite) → the new household summary (201). Blank/too-long ⇒ 400
 * (ApiError); an email already belonging to a member ⇒ 409 (ApiError). `ownerDisplayName` is optional.
 */
export async function createHousehold(
  householdName: string,
  ownerEmail: string,
  ownerDisplayName?: string,
): Promise<HouseholdSummaryDto> {
  return apiSend<HouseholdSummaryDto>(`${HOUSEHOLDS}/`, 'POST', { householdName, ownerEmail, ownerDisplayName: ownerDisplayName || null });
}

// ─── Feedback (dual-mode) ───────────────────────────────────────────────────

/** #4 Feedback for the caller (admin: all; regular: own household) + the isSiteAdmin signal. */
export async function getFeedback(): Promise<FeedbackListDto> {
  return apiGet<FeedbackListDto>(`${FEEDBACK}/`);
}

/** #5 Mark read → 204. Not visible to a non-admin ⇒ 404 (R-C1). */
export async function markFeedbackRead(id: number): Promise<void> {
  await apiSend<void>(`${FEEDBACK}/${id}/read`, 'POST');
}

/** #6 Mark resolved (also read) → 204. Not visible ⇒ 404 (R-C1). */
export async function markFeedbackResolved(id: number): Promise<void> {
  await apiSend<void>(`${FEEDBACK}/${id}/resolve`, 'POST');
}

/** #7 Reopen → 204. Not visible ⇒ 404 (R-C1). */
export async function reopenFeedback(id: number): Promise<void> {
  await apiSend<void>(`${FEEDBACK}/${id}/reopen`, 'POST');
}
