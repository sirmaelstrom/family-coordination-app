import type {
  CategoryListDto,
  CategoryDto,
  CategoryWriteRequest,
  MemberListDto,
  MemberActionDto,
  MemberDto,
} from './types';

const CATEGORIES = '/api/settings/categories';
const MEMBERS = '/api/settings/members';

/**
 * Thrown on any non-2xx response. `status` lets callers react to a rejection.
 *
 * ⚠ Carried from the other islands: the app re-executes empty-body API 4xx
 * through a Blazor `/not-found` page, so a server-side "not found" can arrive
 * as an empty 400 (a bare DELETE 404 even surfaces as 405). Every settings
 * endpoint returns a NON-EMPTY body on 4xx, and the island treats ANY 4xx as a
 * non-retryable client rejection → reconcile (refetch) + a calm toast.
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

// ─── Categories ─────────────────────────────────────────────────────────────

/** #1 Active + deleted categories for the household. */
export async function getCategories(): Promise<CategoryListDto> {
  return request<CategoryListDto>(`${CATEGORIES}/`);
}

/** #2 Create → the saved category (201). Empty name → 400. */
export async function createCategory(body: CategoryWriteRequest): Promise<CategoryDto> {
  return request<CategoryDto>(`${CATEGORIES}/`, { method: 'POST', ...jsonBody(body) });
}

/** #3 Update name/emoji/color (sort order preserved) → the updated category. 404 if missing. */
export async function updateCategory(categoryId: number, body: CategoryWriteRequest): Promise<CategoryDto> {
  return request<CategoryDto>(`${CATEGORIES}/${categoryId}`, { method: 'PUT', ...jsonBody(body) });
}

/** #4 Soft-delete → 204 (idempotent). 404 if missing. */
export async function deleteCategory(categoryId: number): Promise<void> {
  await request<void>(`${CATEGORIES}/${categoryId}`, { method: 'DELETE' });
}

/** #5 Restore a soft-deleted category → 204 (idempotent). */
export async function restoreCategory(categoryId: number): Promise<void> {
  await request<void>(`${CATEGORIES}/${categoryId}/restore`, { method: 'POST' });
}

/** #6 Persist a new order (index ⇒ sortOrder) → 204. */
export async function updateSortOrder(orderedIds: number[]): Promise<void> {
  await request<void>(`${CATEGORIES}/sort-order`, { method: 'PUT', ...jsonBody({ orderedIds }) });
}

/** #7 Whether the category's name is used by any ingredient (for the delete confirm). */
export async function categoryInUse(categoryId: number): Promise<boolean> {
  const res = await request<{ inUse: boolean }>(`${CATEGORIES}/${categoryId}/in-use`);
  return res.inUse;
}

// ─── Members ──────────────────────────────────────────────────────────────────

/** #8 Household members + the caller's id (for "You" + self-gating). */
export async function getMembers(): Promise<MemberListDto> {
  return request<MemberListDto>(`${MEMBERS}/`);
}

/** #9 Add/re-enable by email → outcome envelope (200). Another household ⇒ 409 (ApiError). */
export async function addMember(email: string): Promise<MemberActionDto> {
  return request<MemberActionDto>(`${MEMBERS}/`, { method: 'POST', ...jsonBody({ email }) });
}

/** #10 Enable/disable → the updated member. Self ⇒ 400; last-active ⇒ 409 (ApiError). */
export async function setWhitelist(userId: number, isWhitelisted: boolean): Promise<MemberDto> {
  return request<MemberDto>(`${MEMBERS}/${userId}`, { method: 'PUT', ...jsonBody({ isWhitelisted }) });
}

/** #11 Delete a member → 204. Self ⇒ 400; last-user / has-activity ⇒ 409 (ApiError). */
export async function deleteMember(userId: number): Promise<void> {
  await request<void>(`${MEMBERS}/${userId}`, { method: 'DELETE' });
}
