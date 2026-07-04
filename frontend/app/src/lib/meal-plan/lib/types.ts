// ─────────────────────────────────────────────────────────────────────────
// TS mirror of the meal-plan board DTO contract (M9 four-way lockstep with
// Services/Dtos/MealPlanDtos.cs ↔ Fixtures/MealPlanBoard/board.json ↔
// MealPlanBoardDtoContractTests). A shape/casing change updates all four.
//
// ⚠ CASING: `MealType` / `RecipeType` are real enum TYPES on the C# DTO → they
//   serialize as camelCase string unions via the globally-registered
//   JsonStringEnumConverter(CamelCase). `DateOnly` serializes as "YYYY-MM-DD".
//
// ⚠ DATES: every `date` / `weekStartDate` below is a "YYYY-MM-DD" STRING.
//   NEVER `new Date('YYYY-MM-DD')` for logic — UTC-parsing shifts the day
//   backward in US timezones (global CORRECTION / MN4). Use lib/dates.ts, which
//   parses by splitting on '-' and steps weeks via Date.UTC(...,12).
//
// Versionless / last-write-wins: parity ops are add + remove only, so there is
// NO version/xmin token anywhere on this contract.
// ─────────────────────────────────────────────────────────────────────────

export type MealType = 'breakfast' | 'lunch' | 'dinner' | 'snack';

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

/** The lightweight recipe shape a slot card renders (the board payload stays lean). */
export interface MealRecipeSummaryDto {
  recipeId: number;
  name: string;
  imagePath: string | null;
  recipeType: RecipeType;
}

/** One planned meal. Exactly one of `recipe` / `customMealName` is set. */
export interface MealPlanEntryDto {
  mealPlanId: number;
  entryId: number;
  /** "YYYY-MM-DD" — the slot's calendar position. NEVER new Date() it for logic. */
  date: string;
  mealType: MealType;
  recipe: MealRecipeSummaryDto | null;
  customMealName: string | null;
  notes: string | null;
}

/** The week board: the Monday it covers, the plan id (null when none yet), and its entries. */
export interface MealPlanBoardDto {
  /** "YYYY-MM-DD" — the Monday, echoed by the server. */
  weekStartDate: string;
  mealPlanId: number | null;
  entries: MealPlanEntryDto[];
}

/** One ingredient line, pre-ordered by `sortOrder`. */
export interface RecipeIngredientDto {
  quantity: number | null;
  unit: string | null;
  name: string;
  notes: string | null;
  sortOrder: number;
}

/**
 * Read-only recipe detail for the in-island view modal. `instructionsHtml` is
 * server-sanitized (MarkdownHelper.ToSafeHtml) so the island ships no Markdown
 * library — render via {@html}.
 */
export interface RecipeDetailDto {
  recipeId: number;
  name: string;
  imagePath: string | null;
  recipeType: RecipeType;
  prepTimeMinutes: number | null;
  cookTimeMinutes: number | null;
  servings: number | null;
  instructionsHtml: string;
  ingredients: RecipeIngredientDto[];
}

/** The shell context the Razor host hands the island via root data-attrs. */
export interface ShellContext {
  householdId: number;
  userId: number;
  userName: string;
}
