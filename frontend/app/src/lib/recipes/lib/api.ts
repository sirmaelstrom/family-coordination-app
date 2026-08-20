import type {
  RecipeListDto,
  RecipeFullDto,
  RecipeWriteRequest,
  ParsedIngredientDto,
  CategoryDto,
  ConnectedHouseholdDto,
  RecipeImportPreviewDto,
  RecipeDraftData,
  SaveDraftRequest,
} from './types';
import { ApiError, apiGet, apiSend, apiUpload } from '$lib/api/client';

const BASE = '/api/recipes';

// Transport + error contract live in $lib/api/client (the one HTTP boundary,
// quest 79aa83e7). Recipes semantics: any 4xx → reconcile (refetch the list)
// + a calm toast. Exception: a 409 on the full-form PUT is a stale xmin
// `version` token — the edit store shows a reload banner instead of
// navigating away (see recipeEditStore). The connected-household reads answer
// 403 when not connected — a domain signal, which is why the client boundary
// never redirects on 403.
export { ApiError };

// ─── List + detail ────────────────────────────────────────────────────────

/** #1 Own household's recipe grid + this user's favorite ids. Empty `q` ⇒ all. */
export async function listRecipes(q: string): Promise<RecipeListDto> {
  return apiGet<RecipeListDto>(`${BASE}?q=${encodeURIComponent(q)}`);
}

/** #2 Full recipe (read drawer + edit load — superset). 404 → ApiError. */
export async function getRecipe(recipeId: number): Promise<RecipeFullDto> {
  return apiGet<RecipeFullDto>(`${BASE}/${recipeId}`);
}

/** #3 Create → the saved recipe (201). */
export async function createRecipe(body: RecipeWriteRequest): Promise<RecipeFullDto> {
  return apiSend<RecipeFullDto>(`${BASE}`, 'POST', body);
}

/** #4 Update (replaces ingredients wholesale) → the re-fetched recipe. Stale `version` token ⇒ 409. */
export async function updateRecipe(recipeId: number, body: RecipeWriteRequest): Promise<RecipeFullDto> {
  return apiSend<RecipeFullDto>(`${BASE}/${recipeId}`, 'PUT', body);
}

/** #5 Soft-delete → 204. A missing recipe → 404. */
export async function deleteRecipe(recipeId: number): Promise<void> {
  await apiSend<void>(`${BASE}/${recipeId}`, 'DELETE');
}

/** #6 Toggle favorite → the new state. */
export async function toggleFavorite(recipeId: number): Promise<{ isFavorite: boolean }> {
  return apiSend<{ isFavorite: boolean }>(`${BASE}/${recipeId}/favorite`, 'POST');
}

// ─── Ingredient entry helpers ───────────────────────────────────────────────

/** #7 Ingredient-name autocomplete. `<2` chars ⇒ [] (server guards). */
export async function ingredientSuggestions(prefix: string): Promise<string[]> {
  return apiGet<string[]>(`${BASE}/ingredient-suggestions?prefix=${encodeURIComponent(prefix)}`);
}

/** #8 Server NL-parse one ingredient line (parse-on-blur). Empty text → 400. */
export async function parseIngredient(text: string): Promise<ParsedIngredientDto> {
  return apiSend<ParsedIngredientDto>(`${BASE}/parse-ingredient`, 'POST', { text });
}

/** #9 Bulk-paste preview — parse each non-blank line in one round-trip. */
export async function parseIngredients(lines: string[]): Promise<ParsedIngredientDto[]> {
  return apiSend<ParsedIngredientDto[]>(`${BASE}/parse-ingredients`, 'POST', { lines });
}

/** #10 Household categories for the entry category select. */
export async function getCategories(): Promise<CategoryDto[]> {
  return apiGet<CategoryDto[]>(`${BASE}/categories`);
}

// ─── Images ─────────────────────────────────────────────────────────────────

/**
 * #11 Multipart upload → the stored path. NOT JSON — send FormData and let the
 * browser set the multipart boundary (do not set Content-Type). 10 MB +
 * jpg/png/gif/webp validated server-side (else 400).
 */
export async function uploadImage(file: File): Promise<{ imagePath: string }> {
  const form = new FormData();
  form.append('file', file);
  return apiUpload<{ imagePath: string }>(`${BASE}/images`, form);
}

/** #12 Household image paths for the picker grid. */
export async function listImages(): Promise<string[]> {
  return apiGet<string[]>(`${BASE}/images`);
}

// ─── Import ─────────────────────────────────────────────────────────────────

/**
 * #13 Scrape→preview. Parses the URL WITHOUT persisting — on success returns the
 * parsed recipe payload (create-compatible: confirm by POSTing it to createRecipe).
 * On a duplicate returns existingRecipeId/Name (unless `force`). On failure,
 * errorType + partialData (still previewable). May take ≤60s (Polly) — do NOT add
 * a shorter client timeout.
 */
export async function previewImport(url: string, force = false): Promise<RecipeImportPreviewDto> {
  return apiSend<RecipeImportPreviewDto>(`${BASE}/import/preview`, 'POST', { url, force });
}

// ─── Connected households ─────────────────────────────────────────────────────

/** #14 Connected households for the selector. */
export async function getConnections(): Promise<ConnectedHouseholdDto[]> {
  return apiGet<ConnectedHouseholdDto[]>(`${BASE}/connections`);
}

/** #15 A connected household's shared recipes (read-only; favoriteRecipeIds always []). 403 if not connected. */
export async function listConnectedRecipes(chId: number, q: string): Promise<RecipeListDto> {
  return apiGet<RecipeListDto>(`${BASE}/connected/${chId}?q=${encodeURIComponent(q)}`);
}

/** #16 Read-only detail of a connected recipe (author stripped). 403 if not connected. */
export async function getConnectedRecipe(chId: number, recipeId: number): Promise<RecipeFullDto> {
  return apiGet<RecipeFullDto>(`${BASE}/connected/${chId}/${recipeId}`);
}

/** #17 Copy a connected recipe into my household → the new recipe id (201). */
export async function copyConnectedRecipe(chId: number, recipeId: number): Promise<{ recipeId: number }> {
  return apiSend<{ recipeId: number }>(`${BASE}/connected/${chId}/${recipeId}/copy`, 'POST');
}

// ─── Drafts (autosave) ────────────────────────────────────────────────────────

/** #18 Load a draft. `recipeId` omitted ⇒ the new-recipe draft. 204 (no draft) ⇒ null. */
export async function getDraft(recipeId?: number | null): Promise<RecipeDraftData | null> {
  const qs = recipeId != null ? `?recipeId=${recipeId}` : '';
  const draft = await apiGet<RecipeDraftData | undefined>(`${BASE}/draft${qs}`);
  return draft ?? null;
}

/** #19 Save a draft (flat body: recipeId + draft fields) → 204. */
export async function saveDraft(body: SaveDraftRequest): Promise<void> {
  await apiSend<void>(`${BASE}/draft`, 'PUT', body);
}

/** #20 Delete a draft → 204 (idempotent). `recipeId` omitted ⇒ the new-recipe draft. */
export async function deleteDraft(recipeId?: number | null): Promise<void> {
  const qs = recipeId != null ? `?recipeId=${recipeId}` : '';
  await apiSend<void>(`${BASE}/draft${qs}`, 'DELETE');
}
