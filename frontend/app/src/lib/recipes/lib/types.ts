// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the /api/recipes contract (M9 lockstep). Source of truth:
//   - Services/Dtos/RecipeDtos.cs (the C# records)
//   - tests/.../Fixtures/RecipeList/list.json + Fixtures/RecipeFull/recipe.json
//     (the contract-test tripwires — these keys/casing are byte-locked)
//   - Endpoints/RecipesEndpoints.cs (request DTOs)
//   - Services/DraftService.cs (RecipeDraftData / IngredientDraftData)
//
// ⚠ CASING: all keys are camelCase. `RecipeType` is a real C# enum on the DTO →
//   it serializes as a camelCase STRING via JsonStringEnumConverter(CamelCase)
//   ("main", "side", …). `decimal?`/`int?` serialize as number-or-null.
// ─────────────────────────────────────────────────────────────────────────

export type RecipeType =
  | 'main'
  | 'side'
  | 'appetizer'
  | 'dessert'
  | 'beverage'
  | 'sauce'
  | 'breakfast'
  | 'snack'
  | 'other';

// ── List (Fixtures/RecipeList/list.json) ───────────────────────────────────

export interface RecipeListItemDto {
  recipeId: number;
  name: string;
  recipeType: RecipeType;
  imagePath: string | null;
  /** bool, NOT the url — the card only shows the "imported" cloud icon. */
  hasSourceUrl: boolean;
  /** null when connected (privacy) or the author was deleted. */
  createdByName: string | null;
  /** User has PictureUrl + Initials, NO color → card renders pic-or-initials. */
  createdByPictureUrl: string | null;
  /** First 3 ingredient names (ordered by sort order). */
  ingredientPreview: string[];
  /** Total count — for the "+N more" suffix. */
  ingredientCount: number;
}

export interface RecipeListDto {
  recipes: RecipeListItemDto[];
  /** Own-household only; always [] for a connected household. */
  favoriteRecipeIds: number[];
}

// ── Full (read drawer + edit form superset, D3) (Fixtures/RecipeFull/recipe.json) ──

export interface RecipeIngredientFullDto {
  ingredientId: number;
  quantity: number | null;
  unit: string | null;
  name: string;
  category: string;
  notes: string | null;
  groupName: string | null;
  sortOrder: number;
}

export interface RecipeFullDto {
  recipeId: number;
  name: string;
  recipeType: RecipeType;
  description: string | null;
  /** RAW markdown — for the edit textarea. */
  instructions: string | null;
  /** Server-sanitized HTML — for the read drawer's {@html}. */
  instructionsHtml: string;
  imagePath: string | null;
  sourceUrl: string | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  servings: number | null;
  /** Both null when connected (includeAuthor:false) / deleted user. */
  createdByName: string | null;
  createdByPictureUrl: string | null;
  /** Attribution for copied recipes. */
  sharedFromHouseholdName: string | null;
  ingredients: RecipeIngredientFullDto[];
}

// ── Parse / categories / connections / import ──────────────────────────────

export interface ParsedIngredientDto {
  quantity: number | null;
  unit: string | null;
  name: string;
  notes: string | null;
  isComplete: boolean;
  suggestedCategory: string;
}

export interface CategoryDto {
  name: string;
}

export interface ConnectedHouseholdDto {
  householdId: number;
  householdName: string;
}

export interface PartialRecipeDataDto {
  name: string | null;
  description: string | null;
  instructions: string | null;
  ingredientStrings: string[] | null;
  imageUrl: string | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  servings: number | null;
}

/** Legacy scrape-AND-create result (POST /import — endpoint kept; the SPA now uses /import/preview). */
export interface RecipeImportResultDto {
  success: boolean;
  /** The SAVED recipe id on success. */
  recipeId: number | null;
  errorMessage: string | null;
  /** The RecipeImportErrorType name (e.g. "NetworkError"). */
  errorType: string | null;
  existingRecipeId: number | null;
  existingRecipeName: string | null;
  partialData: PartialRecipeDataDto | null;
}

/** One parsed ingredient of a preview — SAME shape as RecipeIngredientWrite (create-compatible). */
export interface RecipePreviewIngredientDto {
  name: string;
  quantity: number | null;
  unit: string | null;
  category: string;
  notes: string | null;
  groupName: string | null;
  sortOrder: number;
}

/** The parsed-but-UNSAVED recipe — field-compatible with RecipeWriteRequest (send verbatim on confirm). */
export interface RecipePreviewDto {
  name: string;
  description: string | null;
  instructions: string | null;
  sourceUrl: string | null;
  servings: number | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  recipeType: RecipeType;
  imagePath: string | null;
  ingredients: RecipePreviewIngredientDto[];
}

/**
 * POST /import/preview result — scrape + parse WITHOUT persisting. Mirrors RecipeImportResultDto's
 * duplicate/failure surface; success carries `recipe` (the preview payload) instead of a saved id.
 */
export interface RecipeImportPreviewDto {
  success: boolean;
  recipe: RecipePreviewDto | null;
  errorMessage: string | null;
  errorType: string | null;
  existingRecipeId: number | null;
  existingRecipeName: string | null;
  partialData: PartialRecipeDataDto | null;
}

// ── Drafts (mirror DraftService.RecipeDraftData / IngredientDraftData) ──────
// Reused as BOTH the GET /draft response AND (flattened with recipeId) the PUT body.

export interface IngredientDraftData {
  name: string;
  quantity: number | null;
  unit: string | null;
  category: string;
  notes: string | null;
  groupName: string | null;
  sortOrder: number;
}

export interface RecipeDraftData {
  name: string;
  description: string | null;
  instructions: string | null;
  imagePath: string | null;
  sourceUrl: string | null;
  servings: number | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  ingredients: IngredientDraftData[];
}

// ── Write request (POST/PUT body — Endpoints RecipeWriteRequest) ────────────

export interface RecipeIngredientWrite {
  name: string;
  quantity: number | null;
  unit: string | null;
  category: string;
  notes: string | null;
  groupName: string | null;
  sortOrder: number;
}

export interface RecipeWriteRequest {
  name: string;
  description: string | null;
  instructions: string | null;
  sourceUrl: string | null;
  servings: number | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  recipeType: RecipeType;
  imagePath: string | null;
  ingredients: RecipeIngredientWrite[];
}

/** PUT /draft body — FLAT (recipeId + the draft fields). recipeId omitted ⇒ new-recipe draft. */
export interface SaveDraftRequest {
  recipeId: number | null;
  name: string;
  description: string | null;
  instructions: string | null;
  imagePath: string | null;
  sourceUrl: string | null;
  servings: number | null;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  ingredients: IngredientDraftData[];
}

// ── Shell context (read from the root data-attrs) ──────────────────────────

export type IslandView = 'list' | 'edit';

export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
  view: IslandView;
  /** Set only for editing an existing recipe (null for /recipes/new). */
  recipeId: number | null;
}
