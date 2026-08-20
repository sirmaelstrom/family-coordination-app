import type {
  ConnectionsDto,
  InviteDto,
  ValidateResultDto,
  AcceptResultDto,
} from './types';

const BASE = '/api/settings/connections';

// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). Connections semantics: the endpoints return outcome
// envelopes (200) for the expected validate/accept flow results, so a 4xx
// here is a genuine error — treat ANY 4xx as a non-retryable rejection.
import { ApiError, apiGet, apiSend } from '$lib/api/client';

export { ApiError };

/** #1 The active invite (or null) + connected households, in one payload. */
export async function getConnections(): Promise<ConnectionsDto> {
  return apiGet<ConnectionsDto>(`${BASE}/`);
}

/** #2 Generate a new invite (replaces any prior active one) → the code (201). */
export async function generateInvite(): Promise<InviteDto> {
  return apiSend<InviteDto>(`${BASE}/invite`, 'POST');
}

/** #3 Cancel the active invite → 204 (idempotent). */
export async function cancelInvite(): Promise<void> {
  await apiSend<void>(`${BASE}/invite`, 'DELETE');
}

/** #4 Validate a code WITHOUT connecting → 200 outcome envelope. */
export async function validateCode(code: string): Promise<ValidateResultDto> {
  return apiSend<ValidateResultDto>(`${BASE}/validate`, 'POST', { code });
}

/** #5 Accept a code (establish the connection) → 200 outcome envelope. */
export async function acceptCode(code: string): Promise<AcceptResultDto> {
  return apiSend<AcceptResultDto>(`${BASE}/accept`, 'POST', { code });
}

/** #6 Disconnect a connected household → 204 (idempotent; M1 enforced server-side). */
export async function disconnect(householdId: number): Promise<void> {
  await apiSend<void>(`${BASE}/connected/${householdId}`, 'DELETE');
}
