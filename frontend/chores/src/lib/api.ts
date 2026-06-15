import type {
  ChoreBoardDto,
  ChoreDto,
  ChoreSubtaskDto,
  ChoreEquityDto,
  EquityWindow,
  RoomDto,
  EffortTier,
  RecurrenceMode,
  DigestSettingsView,
  DigestSettingsUpdate,
} from './types';

const CHORES_BASE = '/api/chores';
const ROOMS_BASE = '/api/rooms';

/**
 * Thrown on any non-2xx response. `status` lets callers distinguish the
 * retryable concurrency conflict (409) from every other rejection.
 *
 * ⚠ WP-08 finding: the app re-executes empty-body API 404s through a Blazor
 * `/not-found` page, so a server-side "not found" can arrive on the wire as an
 * empty **400**, not 404. Therefore treat ANY 4xx as a non-retryable rejection
 * EXCEPT 409, which is the genuine xmin concurrency conflict (WP-11 refetches
 * the board and retries the mutation on 409 only).
 */
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** The retryable optimistic-concurrency conflict (refetch board + retry). */
  get isConflict(): boolean {
    return this.status === 409;
  }

  /**
   * A non-retryable client rejection: validation / illegal transition / not
   * found (which may surface as an empty 400 per the WP-08 re-execution
   * behavior). Everything 4xx that is NOT 409.
   */
  get isClientRejection(): boolean {
    return this.status >= 400 && this.status < 500 && this.status !== 409;
  }
}

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
    // Same-origin cookie auth — the host page is already authenticated.
    credentials: 'include',
    headers: { Accept: 'application/json', ...(init?.headers ?? {}) },
    ...init,
  });
  if (!res.ok) {
    const text = await res.text().catch(() => '');
    throw new ApiError(res.status, text || res.statusText);
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

// ─── Mutation request/response shapes (typed now for WP-11) ─────────────────

/** Concurrency token echoed on every chore mutation (xmin from ChoreDto.version). */
export interface VersionRequest {
  version: number;
}

export interface CreateChoreRequest {
  name: string;
  description?: string | null;
  roomId?: number | null;
  recurrenceMode: RecurrenceMode;
  intervalDays?: number | null;
  /** ISO date "YYYY-MM-DD" (DateOnly server-side). */
  anchorDate?: string | null;
  /** camelCase [Flags] enum string, e.g. "monday, thursday"; null when not Fixed-weekly. */
  daysOfWeek?: string | null;
  dayOfMonth?: number | null;
  effortTier: EffortTier;
  ownerUserId?: number | null;
  assigneeUserId?: number | null;
  photoPath?: string | null;
  /** Optional emoji/short-code icon; "" or omitted = none. */
  icon?: string;
  /** Number of people required to satisfy the chore. Omit or 1 = normal single-person chore. */
  requiredCount?: number;
  /** Initial named roster for a multi-person chore (X>1) — each seeded as Assigned. Ignored for X=1. */
  assignedUserIds?: number[];
}

/** No assignee — assignment never moves via edit. Carries the version. */
export interface UpdateChoreRequest {
  name: string;
  description?: string | null;
  roomId?: number | null;
  recurrenceMode: RecurrenceMode;
  intervalDays?: number | null;
  anchorDate?: string | null;
  daysOfWeek?: string | null;
  dayOfMonth?: number | null;
  effortTier: EffortTier;
  ownerUserId?: number | null;
  version: number;
  /** council C4 — must round-trip so edit-photo works end-to-end. */
  photoPath?: string | null;
  /** Optional emoji/short-code icon; "" or omitted = none. */
  icon?: string;
  /** Number of distinct contributors required to satisfy the chore (WP-05). Omit or 1 = normal. */
  requiredCount?: number;
}

export interface SeedStarterResponse {
  seeded: boolean;
}

/** targetUserId null ⇒ return-to-pile. */
export interface HandOffRequest {
  targetUserId?: number | null;
  version: number;
}

export interface CompleteRequest {
  note?: string | null;
  photoPath?: string | null;
  version: number;
  /** Co-signers for multi-person chores (WP-05); omit for single-person completions. */
  participantUserIds?: number[];
}

/** Add a named member to a multi-person chore's roster (Assigned). */
export interface AssignRosterRequest {
  subjectUserId: number;
  version: number;
}

/** Leave/remove from a roster. subjectUserId omitted/null ⇒ the caller leaves. */
export interface LeaveRosterRequest {
  subjectUserId?: number | null;
  version: number;
}

export interface PhotoUploadResponse {
  photoPath: string;
}

export interface DefaultViewResponse {
  view: string | null;
}

export interface ReorderRoomsRequest {
  orderedRoomIds: number[];
}

export interface RoomUpsertRequest {
  name: string;
  icon?: string | null;
  photoPath?: string | null;
}

// ─── Board read (the ONE payload all lenses group client-side, M11) ─────────

export async function getBoard(): Promise<ChoreBoardDto> {
  return request<ChoreBoardDto>(`${CHORES_BASE}/board`);
}

// ─── Equity read (separate cached payload — the ONLY non-board fetcher, M11) ──
// The four v1.0 lenses group the one board payload client-side; the Equity lens
// is the sole lens with its own endpoint. credentials:include like getBoard.

export async function getEquity(
  window: EquityWindow = 'week',
): Promise<ChoreEquityDto> {
  return request<ChoreEquityDto>(`${CHORES_BASE}/equity?window=${window}`);
}

// ─── Chore mutations (WP-11 wires these to optimistic UI + 409 retry) ───────

export async function createChore(body: CreateChoreRequest): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/`, { method: 'POST', ...jsonBody(body) });
}

export async function updateChore(
  choreId: number,
  body: UpdateChoreRequest,
): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}`, { method: 'PUT', ...jsonBody(body) });
}

export async function deleteChore(choreId: number, version: number): Promise<void> {
  await request<void>(`${CHORES_BASE}/${choreId}`, {
    method: 'DELETE',
    ...jsonBody({ version } satisfies VersionRequest),
  });
}

export async function claimChore(choreId: number, version: number): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/claim`, {
    method: 'POST',
    ...jsonBody({ version } satisfies VersionRequest),
  });
}

/** Take a chore held by someone else — assign it to the caller as a self-claim (displaces the holder). */
export async function takeChore(choreId: number, version: number): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/take`, {
    method: 'POST',
    ...jsonBody({ version } satisfies VersionRequest),
  });
}

export async function dropChore(choreId: number, version: number): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/drop`, {
    method: 'POST',
    ...jsonBody({ version } satisfies VersionRequest),
  });
}

export async function handOffChore(
  choreId: number,
  body: HandOffRequest,
): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/handoff`, {
    method: 'POST',
    ...jsonBody(body),
  });
}

export async function completeChore(
  choreId: number,
  body: CompleteRequest,
): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/complete`, {
    method: 'POST',
    ...jsonBody(body),
  });
}

// ─── Roster mutations (multi-person named soft roster, rework) ──────────────
// Each returns the projected ChoreDto; the store reconciles via the board GET
// for the authoritative roster (the single-chore response carries an empty one).

/** Assign a named member to a multi-person chore's roster (Assigned — a declinable pre-opt-in). */
export async function assignRoster(
  choreId: number,
  subjectUserId: number,
  version: number,
): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/roster/assign`, {
    method: 'POST',
    ...jsonBody({ subjectUserId, version } satisfies AssignRosterRequest),
  });
}

/** Commit the caller to a multi-person chore's roster ("I'm in" — self-opt-in or confirm). */
export async function commitRoster(choreId: number, version: number): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/roster/commit`, {
    method: 'POST',
    ...jsonBody({ version } satisfies VersionRequest),
  });
}

/** Leave/remove from a roster. `subjectUserId` null ⇒ the caller leaves. */
export async function leaveRoster(
  choreId: number,
  subjectUserId: number | null,
  version: number,
): Promise<ChoreDto> {
  return request<ChoreDto>(`${CHORES_BASE}/${choreId}/roster/leave`, {
    method: 'POST',
    ...jsonBody({ subjectUserId, version } satisfies LeaveRosterRequest),
  });
}

// ─── Checklist / subtasks (Phase 14 — versionless / last-write-wins) ────────
// NO version field anywhere: these touch only the subtask row, never the chore's
// xmin. Create/Update return the affected ChoreSubtaskDto; Delete is 204 (no body).

export interface CreateSubtaskRequest {
  title: string;
}

export interface UpdateSubtaskRequest {
  title?: string;
  isDone?: boolean;
  sortOrder?: number;
}

/** Add a checklist item to a chore. POST .../{choreId}/subtasks → the new ChoreSubtaskDto. */
export async function createSubtask(
  choreId: number,
  body: CreateSubtaskRequest,
): Promise<ChoreSubtaskDto> {
  return request<ChoreSubtaskDto>(`${CHORES_BASE}/${choreId}/subtasks`, {
    method: 'POST',
    ...jsonBody(body),
  });
}

/** Patch a checklist item (any subset of title/isDone/sortOrder). PUT → the updated ChoreSubtaskDto. */
export async function updateSubtask(
  choreId: number,
  subtaskId: number,
  body: UpdateSubtaskRequest,
): Promise<ChoreSubtaskDto> {
  return request<ChoreSubtaskDto>(`${CHORES_BASE}/${choreId}/subtasks/${subtaskId}`, {
    method: 'PUT',
    ...jsonBody(body),
  });
}

/** Remove a checklist item. DELETE → 204 (no body). */
export async function deleteSubtask(choreId: number, subtaskId: number): Promise<void> {
  await request<void>(`${CHORES_BASE}/${choreId}/subtasks/${subtaskId}`, { method: 'DELETE' });
}

/** Upload a chore photo (multipart field name `file`) → { photoPath } to pass in create/complete. */
export async function uploadChorePhoto(
  choreId: number,
  file: File,
): Promise<PhotoUploadResponse> {
  const form = new FormData();
  form.append('file', file);
  // No Content-Type header — the browser sets the multipart boundary.
  return request<PhotoUploadResponse>(`${CHORES_BASE}/${choreId}/photo`, {
    method: 'POST',
    body: form,
  });
}

/**
 * Seed the household with starter chores (WP-06 backfill endpoint).
 * Idempotent — a second call returns `{ seeded: false }` (no-op).
 */
export async function seedStarter(): Promise<SeedStarterResponse> {
  return request<SeedStarterResponse>(`${CHORES_BASE}/seed-starter`, { method: 'POST' });
}

/** Persist the caller's roaming default lens (WP-12). null/blank clears to default. */
export async function setDefaultView(view: string | null): Promise<DefaultViewResponse> {
  return request<DefaultViewResponse>(`${CHORES_BASE}/me/default-view`, {
    method: 'PATCH',
    ...jsonBody({ view }),
  });
}

// ─── Digest settings (WP-11) ─────────────────────────────────────────────────
//
// ⚠ MN7 (write-only webhook): GET never returns the webhook URL — only
// hasWebhook + a masked hint. The PUT body carries the URL only when
// webhookAction is 'set'. Never log or render the URL client-side.

/**
 * Fetch the household's digest settings (safe view — no webhook URL).
 * Returns enabled state, cadence, day, hour, hasWebhook + hint, lastSentAt.
 */
export async function getDigestSettings(): Promise<DigestSettingsView> {
  return request<DigestSettingsView>(`${CHORES_BASE}/digest-settings`);
}

/**
 * Persist digest settings. The tri-state `webhookAction` controls the secret:
 *   'keep'  → leave the stored URL unchanged (omit webhookUrl)
 *   'set'   → encrypt + store webhookUrl (include webhookUrl in body)
 *   'clear' → remove the stored URL (omit webhookUrl)
 * Returns the refreshed DigestSettingsView (no URL in response).
 * ⚠ Do NOT log `body.webhookUrl`; do NOT put it in a query string.
 */
export async function updateDigestSettings(
  body: DigestSettingsUpdate,
): Promise<DigestSettingsView> {
  return request<DigestSettingsView>(`${CHORES_BASE}/digest-settings`, {
    method: 'PUT',
    ...jsonBody(body),
  });
}

// ─── Room admin (/api/rooms) ────────────────────────────────────────────────

export async function listRooms(): Promise<RoomDto[]> {
  return request<RoomDto[]>(`${ROOMS_BASE}/`);
}

export async function getRoom(roomId: number): Promise<RoomDto> {
  return request<RoomDto>(`${ROOMS_BASE}/${roomId}`);
}

export async function createRoom(body: RoomUpsertRequest): Promise<RoomDto> {
  return request<RoomDto>(`${ROOMS_BASE}/`, { method: 'POST', ...jsonBody(body) });
}

export async function updateRoom(
  roomId: number,
  body: RoomUpsertRequest,
): Promise<RoomDto> {
  return request<RoomDto>(`${ROOMS_BASE}/${roomId}`, { method: 'PUT', ...jsonBody(body) });
}

export async function deleteRoom(roomId: number): Promise<void> {
  await request<void>(`${ROOMS_BASE}/${roomId}`, { method: 'DELETE' });
}

export async function reorderRooms(orderedRoomIds: number[]): Promise<void> {
  await request<void>(`${ROOMS_BASE}/reorder`, {
    method: 'POST',
    ...jsonBody({ orderedRoomIds } satisfies ReorderRoomsRequest),
  });
}

export async function uploadRoomPhoto(
  roomId: number,
  file: File,
): Promise<PhotoUploadResponse> {
  const form = new FormData();
  form.append('file', file);
  return request<PhotoUploadResponse>(`${ROOMS_BASE}/${roomId}/photo`, {
    method: 'POST',
    body: form,
  });
}
