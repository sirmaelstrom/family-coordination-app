import type {
  ShoppingListDto,
  ShoppingListItemDto,
  ShoppingListSummaryDto,
} from './types';

const BASE = '/api/shopping-lists';

async function request<T>(url: string, init?: RequestInit): Promise<T> {
  const res = await fetch(url, {
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

export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }
}

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
  return request<ShoppingListSummaryDto[]>(`${BASE}/`);
}

export async function createList(name: string): Promise<ShoppingListSummaryDto> {
  return request<ShoppingListSummaryDto>(`${BASE}/`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name }),
  });
}

export async function generateFromMealPlan(
  body: GenerateBody,
): Promise<ShoppingListSummaryDto> {
  return request<ShoppingListSummaryDto>(`${BASE}/actions/generate-from-meal-plan`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export async function getList(listId: number): Promise<ShoppingListDto> {
  return request<ShoppingListDto>(`${BASE}/${listId}`);
}

export async function patchItem(
  listId: number,
  itemId: number,
  body: PatchItemBody,
): Promise<ShoppingListItemDto> {
  return request<ShoppingListItemDto>(`${BASE}/${listId}/items/${itemId}`, {
    method: 'PATCH',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export async function addItem(
  listId: number,
  body: AddItemBody,
): Promise<ShoppingListItemDto> {
  return request<ShoppingListItemDto>(`${BASE}/${listId}/items`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  });
}

export async function deleteItem(listId: number, itemId: number): Promise<void> {
  await request<void>(`${BASE}/${listId}/items/${itemId}`, { method: 'DELETE' });
}

export async function updateSortOrders(
  listId: number,
  updates: SortOrderUpdate[],
): Promise<void> {
  await request<void>(`${BASE}/${listId}/items/sort-orders`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ updates }),
  });
}

export async function toggleFavorite(
  listId: number,
): Promise<{ id: number; isFavorite: boolean }> {
  return request(`${BASE}/${listId}/actions/toggle-favorite`, { method: 'POST' });
}

export async function archiveList(listId: number): Promise<void> {
  await request<void>(`${BASE}/${listId}/actions/archive`, { method: 'POST' });
}

export async function renameList(
  listId: number,
  name: string,
): Promise<{ id: number; name: string }> {
  return request(`${BASE}/${listId}/actions/rename`, {
    method: 'POST',
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify({ name }),
  });
}

export async function clearChecked(listId: number): Promise<{ removed: number }> {
  return request(`${BASE}/${listId}/actions/clear-checked`, { method: 'POST' });
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
