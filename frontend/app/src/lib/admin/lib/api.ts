import type { HouseholdRequestsDto, HouseholdSummaryDto, FeedbackListDto } from './types';

const REQUESTS = '/api/settings/household-requests';
const HOUSEHOLDS = '/api/settings/households';
const FEEDBACK = '/api/settings/feedback';

/**
 * Thrown on any non-2xx response. `status` lets callers react to a rejection.
 *
 * ⚠ Carried from the other islands: the app re-executes empty-body API 4xx
 * through a Blazor `/not-found` page, so a server-side "not found" can arrive
 * as an empty 400 (a bare DELETE 404 even surfaces as 405). Every admin endpoint
 * returns a NON-EMPTY body on 4xx, and the island treats ANY 4xx as a
 * non-retryable client rejection → reconcile (refetch) + a calm toast. The
 * 403 on the households GET is special-cased by the store as the access-denied
 * signal (R-C4 — the 403 IS the signal; no /context endpoint).
 */
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** A non-retryable client rejection (validation / not found / conflict). Any 4xx. */
  get isClientRejection(): boolean {
    return this.status >= 400 && this.status < 500;
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    credentials: 'include',
    headers: { Accept: 'application/json', ...(init?.headers ?? {}) },
    ...init,
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    let message = text || res.statusText;
    // The endpoints return { message } on 4xx — surface it for the toast.
    try {
      const parsed = JSON.parse(text);
      if (parsed && typeof parsed.message === 'string') message = parsed.message;
    } catch { /* not JSON — use the raw text */ }
    throw new ApiError(res.status, message);
  }
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

function jsonBody(body: unknown): RequestInit {
  return {
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  };
}

// ─── Household requests (site-admin only) ───────────────────────────────────

/** #1 Requests (pending-first) + existing households. 403 (ApiError) for a non-admin → access denied. */
export async function getHouseholdRequests(): Promise<HouseholdRequestsDto> {
  return request<HouseholdRequestsDto>(`${REQUESTS}/`);
}

/** #2 Approve → the new household summary (201). Already-reviewed ⇒ 409 (ApiError); unknown ⇒ 404. */
export async function approveRequest(id: number): Promise<HouseholdSummaryDto> {
  return request<HouseholdSummaryDto>(`${REQUESTS}/${id}/approve`, { method: 'POST' });
}

/** #3 Reject with an OPTIONAL reason → 204. Already-reviewed ⇒ 409 (ApiError); unknown ⇒ 404. */
export async function rejectRequest(id: number, reason: string): Promise<void> {
  await request<void>(`${REQUESTS}/${id}/reject`, { method: 'POST', ...jsonBody({ reason }) });
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
  return request<HouseholdSummaryDto>(`${HOUSEHOLDS}/`, {
    method: 'POST',
    ...jsonBody({ householdName, ownerEmail, ownerDisplayName: ownerDisplayName || null }),
  });
}

// ─── Feedback (dual-mode) ───────────────────────────────────────────────────

/** #4 Feedback for the caller (admin: all; regular: own household) + the isSiteAdmin signal. */
export async function getFeedback(): Promise<FeedbackListDto> {
  return request<FeedbackListDto>(`${FEEDBACK}/`);
}

/** #5 Mark read → 204. Not visible to a non-admin ⇒ 404 (R-C1). */
export async function markFeedbackRead(id: number): Promise<void> {
  await request<void>(`${FEEDBACK}/${id}/read`, { method: 'POST' });
}

/** #6 Mark resolved (also read) → 204. Not visible ⇒ 404 (R-C1). */
export async function markFeedbackResolved(id: number): Promise<void> {
  await request<void>(`${FEEDBACK}/${id}/resolve`, { method: 'POST' });
}

/** #7 Reopen → 204. Not visible ⇒ 404 (R-C1). */
export async function reopenFeedback(id: number): Promise<void> {
  await request<void>(`${FEEDBACK}/${id}/reopen`, { method: 'POST' });
}
