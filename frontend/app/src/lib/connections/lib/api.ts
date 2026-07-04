import type {
  ConnectionsDto,
  InviteDto,
  ValidateResultDto,
  AcceptResultDto,
} from './types';

const BASE = '/api/settings/connections';

/**
 * Thrown on any non-2xx response. `status` lets callers react to a rejection.
 *
 * ⚠ Carried from the other islands: the app re-executes empty-body API 4xx
 * through a Blazor `/not-found` page, so a server-side "not found" can arrive
 * as an empty 400 (a bare DELETE 404 even surfaces as 405). The connections
 * endpoints return outcome envelopes (200) for the expected validate/accept flow
 * results, so a 4xx here is a genuine error (or 401) — treat ANY 4xx as a
 * non-retryable client rejection.
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

/** #1 The active invite (or null) + connected households, in one payload. */
export async function getConnections(): Promise<ConnectionsDto> {
  return request<ConnectionsDto>(`${BASE}/`);
}

/** #2 Generate a new invite (replaces any prior active one) → the code (201). */
export async function generateInvite(): Promise<InviteDto> {
  return request<InviteDto>(`${BASE}/invite`, { method: 'POST' });
}

/** #3 Cancel the active invite → 204 (idempotent). */
export async function cancelInvite(): Promise<void> {
  await request<void>(`${BASE}/invite`, { method: 'DELETE' });
}

/** #4 Validate a code WITHOUT connecting → 200 outcome envelope. */
export async function validateCode(code: string): Promise<ValidateResultDto> {
  return request<ValidateResultDto>(`${BASE}/validate`, { method: 'POST', ...jsonBody({ code }) });
}

/** #5 Accept a code (establish the connection) → 200 outcome envelope. */
export async function acceptCode(code: string): Promise<AcceptResultDto> {
  return request<AcceptResultDto>(`${BASE}/accept`, { method: 'POST', ...jsonBody({ code }) });
}

/** #6 Disconnect a connected household → 204 (idempotent; M1 enforced server-side). */
export async function disconnect(householdId: number): Promise<void> {
  await request<void>(`${BASE}/connected/${householdId}`, { method: 'DELETE' });
}
