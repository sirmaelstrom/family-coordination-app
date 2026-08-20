import type {
  MealPlanBoardDto,
  MealPlanEntryDto,
  MealRecipeSummaryDto,
  RecipeDetailDto,
  MealType,
  RecipeType,
} from './types';
import { ApiError, apiGet, apiSend } from '$lib/api/client';

const BASE = '/api/meal-plan';

// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). Meal-plan semantics: entry mutations carry the xmin token
// and a stale one is a 409 (PR #95); every 4xx → refetch the week + calm
// toast, no automatic retry — the 409 only changes what the toast says
// (callers branch on `e.status === 409` directly).
export { ApiError };

// ─── Board read ─────────────────────────────────────────────────────────────
// The server re-snaps `weekStart` to that week's Monday and echoes it back as
// `weekStartDate`, so client stepping is display-only (the server is the
// authority on the week boundary). A "YYYY-MM-DD" is always sent.

export async function getBoard(weekStart: string): Promise<MealPlanBoardDto> {
  return apiGet<MealPlanBoardDto>(`${BASE}/board?weekStart=${encodeURIComponent(weekStart)}`);
}

// ─── Entries (add is versionless; move / servings / remove carry the xmin token) ──

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
  return apiSend<MealPlanEntryDto>(`${BASE}/entries`, 'POST', body);
}

/** Body for PATCH /entries/{mealPlanId}/{entryId} — the target slot (same week only). */
export interface MoveEntryBody {
  /** "YYYY-MM-DD" — must fall inside the entry's plan week (else a 400). */
  date: string;
  mealType: MealType;
  /** The entry's xmin token, straight off the board. Stale ⇒ 409. */
  version: number;
}

/**
 * Move an entry to another same-week slot (drag-to-assign) → the updated entry.
 * Cross-week target / duplicate-in-slot → 400; a missing entry → 404.
 */
export async function moveEntry(
  mealPlanId: number,
  entryId: number,
  body: MoveEntryBody,
): Promise<MealPlanEntryDto> {
  return apiSend<MealPlanEntryDto>(`${BASE}/entries/${mealPlanId}/${entryId}`, 'PATCH', body);
}

/**
 * Set how many people a planned meal is being cooked for; `null` clears it back to the recipe as written.
 * Shopping-list generation scales that entry's ingredients by `servings / recipe.servings`.
 * A non-positive number → 400; a missing entry → 404.
 */
export async function setEntryServings(
  mealPlanId: number,
  entryId: number,
  servings: number | null,
  version: number,
): Promise<MealPlanEntryDto> {
  return apiSend<MealPlanEntryDto>(`${BASE}/entries/${mealPlanId}/${entryId}/servings`, 'PATCH', { servings, version });
}

/**
 * Remove an entry. DELETE → 204 (no body); a missing entry → 404; a stale version → 409.
 * The version travels in the body, matching the chores DELETE (the house pattern).
 */
export async function removeEntry(
  mealPlanId: number,
  entryId: number,
  version: number,
): Promise<void> {
  await apiSend<void>(`${BASE}/entries/${mealPlanId}/${entryId}`, 'DELETE', { version });
}

// ─── Recipes (picker search / quick-create / detail) ─────────────────────────

/** Picker autocomplete. Empty `q` ⇒ all (matches the current MinCharacters=0). */
export async function searchRecipes(q: string): Promise<MealRecipeSummaryDto[]> {
  return apiGet<MealRecipeSummaryDto[]>(`${BASE}/recipes?q=${encodeURIComponent(q)}`);
}

/** Body for POST /recipes — quick-create a bare recipe (details added later). */
export interface QuickCreateRecipeBody {
  name: string;
  recipeType: RecipeType;
}

/** "New Recipe" tab → the created recipe summary (201). The caller then adds an entry with the new id. */
export async function quickCreateRecipe(body: QuickCreateRecipeBody): Promise<MealRecipeSummaryDto> {
  return apiSend<MealRecipeSummaryDto>(`${BASE}/recipes`, 'POST', body);
}

/** Recipe-detail modal (read-only, lazy on view-click → keeps the board lean). */
export async function getRecipeDetail(recipeId: number): Promise<RecipeDetailDto> {
  return apiGet<RecipeDetailDto>(`${BASE}/recipes/${recipeId}`);
}
