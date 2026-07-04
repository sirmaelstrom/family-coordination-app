// Display labels for the RecipeType union (mirrors the Blazor GetRecipeTypeLabel
// in RecipePickerDialog) + the picker's select option order.
import type { RecipeType } from './types';

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

/** The select option order — matches the C# enum declaration order. */
export const RECIPE_TYPES: RecipeType[] = [
  'main',
  'side',
  'appetizer',
  'dessert',
  'beverage',
  'sauce',
  'breakfast',
  'snack',
  'other',
];

/** Short chip label shown on a slot card (the type chip is hidden for `main`). */
export function recipeTypeLabel(type: RecipeType): string {
  return RECIPE_TYPE_LABELS[type] ?? type;
}
