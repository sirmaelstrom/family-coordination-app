import type {
  ArchivedListSummaryDto,
  ShoppingListDto,
  ShoppingListItemDto,
  ShoppingListSummaryDto,
} from './types';
import { ApiError, apiGet, apiSend } from '$lib/api/client';

const BASE = '/api/shopping-lists';

// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). This island's local ApiError copy had no isConflict /
// isClientRejection getters — its callers hand-rolled status checks; the
// canonical class restores them.
export { ApiError };

export interface PatchItemBody {
  isChecked?: boolean;
  quantity?: number | null;
  unit?: string | null;
  name?: string;
  category?: string;
}

export interface AddItemBody {
  name: string;
  quantity?: number | null;
  unit?: string | null;
  category?: string | null;
}

export interface SortOrderUpdate {
  itemId: number;
  sortOrder: number;
  category: string;
}

export interface GenerateBody {
  startDate: string; // YYYY-MM-DD
  endDate: string;
  name?: string;
}

export async function listLists(): Promise<ShoppingListSummaryDto[]> {
  return apiGet<ShoppingListSummaryDto[]>(`${BASE}/`);
}

export async function createList(name: string): Promise<ShoppingListSummaryDto> {
  return apiSend<ShoppingListSummaryDto>(`${BASE}/`, 'POST', { name });
}

export async function generateFromMealPlan(
  body: GenerateBody,
): Promise<ShoppingListSummaryDto> {
  return apiSend<ShoppingListSummaryDto>(`${BASE}/actions/generate-from-meal-plan`, 'POST', body);
}

export async function getList(listId: number): Promise<ShoppingListDto> {
  return apiGet<ShoppingListDto>(`${BASE}/${listId}`);
}

export async function patchItem(
  listId: number,
  itemId: number,
  body: PatchItemBody,
): Promise<ShoppingListItemDto> {
  return apiSend<ShoppingListItemDto>(`${BASE}/${listId}/items/${itemId}`, 'PATCH', body);
}

export async function addItem(
  listId: number,
  body: AddItemBody,
): Promise<ShoppingListItemDto> {
  return apiSend<ShoppingListItemDto>(`${BASE}/${listId}/items`, 'POST', body);
}

export async function deleteItem(listId: number, itemId: number): Promise<void> {
  await apiSend<void>(`${BASE}/${listId}/items/${itemId}`, 'DELETE');
}

export async function updateSortOrders(
  listId: number,
  updates: SortOrderUpdate[],
): Promise<void> {
  await apiSend<void>(`${BASE}/${listId}/items/sort-orders`, 'POST', { updates });
}

export async function toggleFavorite(
  listId: number,
): Promise<{ id: number; isFavorite: boolean }> {
  return apiSend(`${BASE}/${listId}/actions/toggle-favorite`, 'POST');
}

export async function archiveList(listId: number): Promise<void> {
  await apiSend<void>(`${BASE}/${listId}/actions/archive`, 'POST');
}

export async function listArchived(favoritesOnly = false): Promise<ArchivedListSummaryDto[]> {
  const query = favoritesOnly ? '?favoritesOnly=true' : '';
  return apiGet<ArchivedListSummaryDto[]>(`${BASE}/archived${query}`);
}

/** Read-only detail for an ARCHIVED list — the pick-items-off surface. Active lists 404 here. */
export async function getArchivedList(listId: number): Promise<ShoppingListDto> {
  return apiGet<ShoppingListDto>(`${BASE}/archived/${listId}`);
}

/** Reopen an archived list. Flips IsArchived only — no auto-regenerate; the meal-plan link is kept. */
export async function restoreList(listId: number): Promise<void> {
  await apiSend<void>(`${BASE}/${listId}/actions/restore`, 'POST');
}

/** Permanent. Server rejects non-archived lists with 409 — archive first. */
export async function deleteList(listId: number): Promise<void> {
  await apiSend<void>(`${BASE}/${listId}`, 'DELETE');
}

/** Rebuild generated rows from the linked meal plan (checked-state + quantity edits carry). */
export async function regenerateList(listId: number): Promise<ShoppingListDto> {
  return apiSend<ShoppingListDto>(`${BASE}/${listId}/actions/regenerate`, 'POST');
}

export async function renameList(
  listId: number,
  name: string,
): Promise<{ id: number; name: string }> {
  return apiSend(`${BASE}/${listId}/actions/rename`, 'POST', { name });
}

export async function clearChecked(listId: number): Promise<{ removed: number }> {
  return apiSend(`${BASE}/${listId}/actions/clear-checked`, 'POST');
}

export const STANDARD_CATEGORIES = [
  'Produce',
  'Bakery',
  'Meat',
  'Dairy',
  'Frozen',
  'Pantry',
  'Spices',
  'Beverages',
  'Other',
] as const;
