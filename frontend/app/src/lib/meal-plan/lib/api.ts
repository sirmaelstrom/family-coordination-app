import type {
  MealPlanBoardDto,
  MealPlanEntryDto,
  MealRecipeSummaryDto,
  RecipeDetailDto,
  MealType,
  RecipeType,
} from './types';

const BASE = '/api/meal-plan';

/**
 * Thrown on any non-2xx response. `status` lets callers react to a rejection.
 *
 * ⚠ WP-08 finding (carried from chores/shopping-list): the app re-executes
 * empty-body API 404s through a Blazor `/not-found` page, so a server-side
 * "not found" can arrive on the wire as an empty **400**, not 404. Since the
 * meal-plan island is VERSIONLESS (no 409 concurrency dance), the rule is even
 * simpler: treat ANY 4xx as a non-retryable client rejection → refetch the
 * week + calm toast. There is no retry branch.
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
   * A non-retryable client rejection: validation / not found (which may surface
   * as an empty 400 per the WP-08 re-execution behavior). Any 4xx.
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

// ─── Board read ─────────────────────────────────────────────────────────────
// The server re-snaps `weekStart` to that week's Monday and echoes it back as
// `weekStartDate`, so client stepping is display-only (the server is the
// authority on the week boundary). A "YYYY-MM-DD" is always sent.

export async function getBoard(weekStart: string): Promise<MealPlanBoardDto> {
  return request<MealPlanBoardDto>(`${BASE}/board?weekStart=${encodeURIComponent(weekStart)}`);
}

// ─── Entries (add / remove — versionless) ────────────────────────────────────

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
