// ─────────────────────────────────────────────────────────────────────────
// The shared recipe/meal vocabulary — the ONE declaration (quest ca10904a folded
// the byte-identical per-island copies). The unions derive from WIRE_ENUMS, which
// is list-equality-pinned to the server's Enum.GetValues (wire-enums fixture), so
// a new C# member reaches these types through exactly one edit.
//
// Island-specific presentation (short chip labels, chip colors) stays in the
// island that renders it — recipes/lib/recipeType.ts re-exports this surface and
// keeps its own extras.
// ─────────────────────────────────────────────────────────────────────────
import { WIRE_ENUMS } from './contracts';

export type RecipeType = (typeof WIRE_ENUMS.RecipeType)[number];
export type MealType = (typeof WIRE_ENUMS.MealType)[number];

export const RECIPE_TYPE_LABELS: Record<RecipeType, string> = {
  main: 'Main Dish',
  side: 'Side Dish',
  appetizer: 'Appetizer',
  dessert: 'Dessert',
  beverage: 'Beverage',
  sauce: 'Sauce/Condiment',
  breakfast: 'Breakfast',
  snack: 'Snack',
  other: 'Other',
};

/** The select option order — the C# enum declaration order, straight from the pinned list. */
export const RECIPE_TYPES: readonly RecipeType[] = WIRE_ENUMS.RecipeType;

export function recipeTypeLabel(type: RecipeType): string {
  return RECIPE_TYPE_LABELS[type] ?? type;
}
