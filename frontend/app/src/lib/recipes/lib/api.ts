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

const BASE = '/api/recipes';

/**
 * Thrown on any non-2xx response. `status` lets callers react to a rejection.
 *
 * ⚠ Carried from chores/shopping-list/meal-plan: the app re-executes empty-body
 * API 404s through a Blazor `/not-found` page, so a server-side "not found" can
 * arrive on the wire as an empty **400** (a bare DELETE 404 even surfaces as
 * 405). Every recipes endpoint therefore returns a NON-EMPTY body on not-found,
 * and the island treats ANY 4xx as a non-retryable client rejection → reconcile
 * (refetch the list) + a calm toast. Exception: a **409** on the full-form PUT
 * is a stale xmin `version` token — the edit store shows a reload banner
 * instead of navigating away (see recipeEditStore).
 */
export class ApiError extends Error {
  constructor(
    public status: number,
    message: string,
  ) {
    super(message);
    this.name = 'ApiError';
  }

  /** A non-retryable client rejection (validation / not found, incl. the empty-400 quirk). Any 4xx. */
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
  // 204 No-Content (delete, save-draft, and the "no draft" GET) ⇒ undefined.
  if (res.status === 204) return undefined as T;
  return (await res.json()) as T;
}

function jsonBody(body: unknown): RequestInit {
  return {
    headers: { 'Content-Type': 'application/json' },
    body: JSON.stringify(body),
  };
}

// ─── List + detail ────────────────────────────────────────────────────────

/** #1 Own household's recipe grid + this user's favorite ids. Empty `q` ⇒ all. */
export async function listRecipes(q: string): Promise<RecipeListDto> {
  return request<RecipeListDto>(`${BASE}?q=${encodeURIComponent(q)}`);
}

/** #2 Full recipe (read drawer + edit load — superset). 404 → ApiError. */
export async function getRecipe(recipeId: number): Promise<RecipeFullDto> {
  return request<RecipeFullDto>(`${BASE}/${recipeId}`);
}

/** #3 Create → the saved recipe (201). */
export async function createRecipe(body: RecipeWriteRequest): Promise<RecipeFullDto> {
  return request<RecipeFullDto>(`${BASE}`, { method: 'POST', ...jsonBody(body) });
}

/** #4 Update (replaces ingredients wholesale) → the re-fetched recipe. Stale `version` token ⇒ 409. */
export async function updateRecipe(recipeId: number, body: RecipeWriteRequest): Promise<RecipeFullDto> {
  return request<RecipeFullDto>(`${BASE}/${recipeId}`, { method: 'PUT', ...jsonBody(body) });
}

/** #5 Soft-delete → 204. A missing recipe → 404/empty-400. */
export async function deleteRecipe(recipeId: number): Promise<void> {
  await request<void>(`${BASE}/${recipeId}`, { method: 'DELETE' });
}

/** #6 Toggle favorite → the new state. */
export async function toggleFavorite(recipeId: number): Promise<{ isFavorite: boolean }> {
  return request<{ isFavorite: boolean }>(`${BASE}/${recipeId}/favorite`, { method: 'POST' });
}

// ─── Ingredient entry helpers ───────────────────────────────────────────────

/** #7 Ingredient-name autocomplete. `<2` chars ⇒ [] (server guards). */
export async function ingredientSuggestions(prefix: string): Promise<string[]> {
  return request<string[]>(`${BASE}/ingredient-suggestions?prefix=${encodeURIComponent(prefix)}`);
}

/** #8 Server NL-parse one ingredient line (parse-on-blur). Empty text → 400. */
export async function parseIngredient(text: string): Promise<ParsedIngredientDto> {
  return request<ParsedIngredientDto>(`${BASE}/parse-ingredient`, { method: 'POST', ...jsonBody({ text }) });
}

/** #9 Bulk-paste preview — parse each non-blank line in one round-trip. */
export async function parseIngredients(lines: string[]): Promise<ParsedIngredientDto[]> {
  return request<ParsedIngredientDto[]>(`${BASE}/parse-ingredients`, { method: 'POST', ...jsonBody({ lines }) });
}

/** #10 Household categories for the entry category select. */
export async function getCategories(): Promise<CategoryDto[]> {
  return request<CategoryDto[]>(`${BASE}/categories`);
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
  return request<{ imagePath: string }>(`${BASE}/images`, { method: 'POST', body: form });
}

/** #12 Household image paths for the picker grid. */
export async function listImages(): Promise<string[]> {
  return request<string[]>(`${BASE}/images`);
}

// ─── Import ─────────────────────────────────────────────────────────────────

/**
 * #13 Scrape→preview. Parses the URL WITHOUT persisting — on success returns the
 * parsed recipe payload (create-compatible: confirm by POSTing it to createRecipe).
 * On a duplicate returns existingRecipeId/Name (unless `force`). On failure,
 * errorType + partialData (still previewable). May take ≤60s (Polly) — do NOT add
 * a shorter client timeout. (The legacy scrape-and-create POST /import still
 * exists server-side but the SPA no longer calls it.)
 */
export async function previewImport(url: string, force = false): Promise<RecipeImportPreviewDto> {
  return request<RecipeImportPreviewDto>(`${BASE}/import/preview`, { method: 'POST', ...jsonBody({ url, force }) });
}

// ─── Connected households ─────────────────────────────────────────────────────

/** #14 Connected households for the selector. */
export async function getConnections(): Promise<ConnectedHouseholdDto[]> {
  return request<ConnectedHouseholdDto[]>(`${BASE}/connections`);
}

/** #15 A connected household's shared recipes (read-only; favoriteRecipeIds always []). 403 if not connected. */
export async function listConnectedRecipes(chId: number, q: string): Promise<RecipeListDto> {
  return request<RecipeListDto>(`${BASE}/connected/${chId}?q=${encodeURIComponent(q)}`);
}

/** #16 Read-only detail of a connected recipe (author stripped). 403 if not connected. */
export async function getConnectedRecipe(chId: number, recipeId: number): Promise<RecipeFullDto> {
  return request<RecipeFullDto>(`${BASE}/connected/${chId}/${recipeId}`);
}

/** #17 Copy a connected recipe into my household → the new recipe id (201). */
export async function copyConnectedRecipe(chId: number, recipeId: number): Promise<{ recipeId: number }> {
  return request<{ recipeId: number }>(`${BASE}/connected/${chId}/${recipeId}/copy`, { method: 'POST' });
}

// ─── Drafts (autosave) ────────────────────────────────────────────────────────

/** #18 Load a draft. `recipeId` omitted ⇒ the new-recipe draft. 204 (no draft) ⇒ null. */
export async function getDraft(recipeId?: number | null): Promise<RecipeDraftData | null> {
  const qs = recipeId != null ? `?recipeId=${recipeId}` : '';
  const draft = await request<RecipeDraftData | undefined>(`${BASE}/draft${qs}`);
  return draft ?? null;
}

/** #19 Save a draft (flat body: recipeId + draft fields) → 204. */
export async function saveDraft(body: SaveDraftRequest): Promise<void> {
  await request<void>(`${BASE}/draft`, { method: 'PUT', ...jsonBody(body) });
}

/** #20 Delete a draft → 204 (idempotent). `recipeId` omitted ⇒ the new-recipe draft. */
export async function deleteDraft(recipeId?: number | null): Promise<void> {
  const qs = recipeId != null ? `?recipeId=${recipeId}` : '';
  await request<void>(`${BASE}/draft${qs}`, { method: 'DELETE' });
}
