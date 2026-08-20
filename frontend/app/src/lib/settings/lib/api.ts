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

// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). Settings semantics: every endpoint writes a specific 4xx
// body; the island treats ANY 4xx as a non-retryable rejection → reconcile
// (refetch) + a calm toast.
import { ApiError, apiGet, apiSend } from '$lib/api/client';

export { ApiError };

// ─── Categories ─────────────────────────────────────────────────────────────

/** #1 Active + deleted categories for the household. */
export async function getCategories(): Promise<CategoryListDto> {
  return apiGet<CategoryListDto>(`${CATEGORIES}/`);
}

/** #2 Create → the saved category (201). Empty name → 400. */
export async function createCategory(body: CategoryWriteRequest): Promise<CategoryDto> {
  return apiSend<CategoryDto>(`${CATEGORIES}/`, 'POST', body);
}

/** #3 Update name/emoji/color (sort order preserved) → the updated category. 404 if missing. */
export async function updateCategory(categoryId: number, body: CategoryWriteRequest): Promise<CategoryDto> {
  return apiSend<CategoryDto>(`${CATEGORIES}/${categoryId}`, 'PUT', body);
}

/** #4 Soft-delete → 204 (idempotent). 404 if missing. */
export async function deleteCategory(categoryId: number): Promise<void> {
  await apiSend<void>(`${CATEGORIES}/${categoryId}`, 'DELETE');
}

/** #5 Restore a soft-deleted category → 204 (idempotent). */
export async function restoreCategory(categoryId: number): Promise<void> {
  await apiSend<void>(`${CATEGORIES}/${categoryId}/restore`, 'POST');
}

/** #6 Persist a new order (index ⇒ sortOrder) → 204. */
export async function updateSortOrder(orderedIds: number[]): Promise<void> {
  await apiSend<void>(`${CATEGORIES}/sort-order`, 'PUT', { orderedIds });
}

/** #7 Whether the category's name is used by any ingredient (for the delete confirm). */
export async function categoryInUse(categoryId: number): Promise<boolean> {
  const res = await apiGet<{ inUse: boolean }>(`${CATEGORIES}/${categoryId}/in-use`);
  return res.inUse;
}

// ─── Members ──────────────────────────────────────────────────────────────────

/** #8 Household members + the caller's id (for "You" + self-gating). */
export async function getMembers(): Promise<MemberListDto> {
  return apiGet<MemberListDto>(`${MEMBERS}/`);
}

/** #9 Add/re-enable by email → outcome envelope (200). Another household ⇒ 409 (ApiError). */
export async function addMember(email: string): Promise<MemberActionDto> {
  return apiSend<MemberActionDto>(`${MEMBERS}/`, 'POST', { email });
}

/** #10 Enable/disable → the updated member. Self ⇒ 400; last-active ⇒ 409 (ApiError). */
export async function setWhitelist(userId: number, isWhitelisted: boolean): Promise<MemberDto> {
  return apiSend<MemberDto>(`${MEMBERS}/${userId}`, 'PUT', { isWhitelisted });
}

/** #11 Delete a member → 204. Self ⇒ 400; last-user / has-activity ⇒ 409 (ApiError). */
export async function deleteMember(userId: number): Promise<void> {
  await apiSend<void>(`${MEMBERS}/${userId}`, 'DELETE');
}
