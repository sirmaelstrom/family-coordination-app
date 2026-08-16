import type {
  MealPlanBoardDto,
  MealPlanEntryDto,
  MealRecipeSummaryDto,
  RecipeDetailDto,
  MealType,
  RecipeType,
} from './types';
import { messageFrom } from '$lib/shared/api-message';

const BASE = '/api/meal-plan';

/**
 * Thrown on any non-2xx response. `status` lets callers react to a rejection.
 *
 * Since PR #90 an /api 4xx keeps its real status and always carries a JSON
 * `{ message }`. The meal-plan island is VERSIONLESS (no 409 concurrency
 * dance), so the rule is simple: treat ANY 4xx as a non-retryable client
 * rejection → refetch the week + calm toast. There is no retry branch.
 */
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /**
   * A non-retryable client rejection: validation / not found. Any 4xx.
   */
  get isClientRejection(): boolean {
    return this.status >= 400 && this.status < 500;
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
    throw new ApiError(res.status, messageFrom(text) ?? res.statusText);
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

// ─── Board read ─────────────────────────────────────────────────────────────
// The server re-snaps `weekStart` to that week's Monday and echoes it back as
// `weekStartDate`, so client stepping is display-only (the server is the
// authority on the week boundary). A "YYYY-MM-DD" is always sent.

export async function getBoard(weekStart: string): Promise<MealPlanBoardDto> {
  return request<MealPlanBoardDto>(`${BASE}/board?weekStart=${encodeURIComponent(weekStart)}`);
}

// ─── Entries (add / move / remove — versionless) ─────────────────────────────

/** Body for POST /entries — supply EXACTLY one of recipeId / customMealName. */
export interface AddEntryBody {
  /** "YYYY-MM-DD" — the slot's calendar position (server derives the week). */
  date: string;
  mealType: MealType;
  recipeId?: number | null;
  customMealName?: string | null;
  notes?: string | null;
}

/** Add a meal to a slot → the created entry (201). */
export async function addEntry(body: AddEntryBody): Promise<MealPlanEntryDto> {
  return request<MealPlanEntryDto>(`${BASE}/entries`, { method: 'POST', ...jsonBody(body) });
}

/** Body for PATCH /entries/{mealPlanId}/{entryId} — the target slot (same week only). */
export interface MoveEntryBody {
  /** "YYYY-MM-DD" — must fall inside the entry's plan week (else a 400). */
  date: string;
  mealType: MealType;
}

/**
 * Move an entry to another same-week slot (drag-to-assign) → the updated entry.
 * Cross-week target / duplicate-in-slot → 400; a missing entry → 404/empty-400.
 */
export async function moveEntry(
  mealPlanId: number,
  entryId: number,
  body: MoveEntryBody,
): Promise<MealPlanEntryDto> {
  return request<MealPlanEntryDto>(`${BASE}/entries/${mealPlanId}/${entryId}`, {
    method: 'PATCH',
    ...jsonBody(body),
  });
}

/** Remove an entry. DELETE → 204 (no body); a missing entry → 404/empty-400. */
export async function removeEntry(mealPlanId: number, entryId: number): Promise<void> {
  await request<void>(`${BASE}/entries/${mealPlanId}/${entryId}`, { method: 'DELETE' });
}

// ─── Recipes (picker search / quick-create / detail) ─────────────────────────

/** Picker autocomplete. Empty `q` ⇒ all (matches the current MinCharacters=0). */
export async function searchRecipes(q: string): Promise<MealRecipeSummaryDto[]> {
  return request<MealRecipeSummaryDto[]>(`${BASE}/recipes?q=${encodeURIComponent(q)}`);
}

/** Body for POST /recipes — quick-create a bare recipe (details added later). */
export interface QuickCreateRecipeBody {
  name: string;
  recipeType: RecipeType;
}

/** "New Recipe" tab → the created recipe summary (201). The caller then adds an entry with the new id. */
export async function quickCreateRecipe(body: QuickCreateRecipeBody): Promise<MealRecipeSummaryDto> {
  return request<MealRecipeSummaryDto>(`${BASE}/recipes`, { method: 'POST', ...jsonBody(body) });
}

/** Recipe-detail modal (read-only, lazy on view-click → keeps the board lean). */
export async function getRecipeDetail(recipeId: number): Promise<RecipeDetailDto> {
  return request<RecipeDetailDto>(`${BASE}/recipes/${recipeId}`);
}
