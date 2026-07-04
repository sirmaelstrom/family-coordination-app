// Display labels for the RecipeType union (mirrors the Blazor GetRecipeTypeLabel
// in RecipeEdit / RecipeCard) + the type-select option order (the C# enum order).
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

export function recipeTypeLabel(type: RecipeType): string {
  return RECIPE_TYPE_LABELS[type] ?? type;
}

/** Short chip labels for the card (mirrors RecipeCard.razor GetTypeLabel; `main` chip is hidden). */
const RECIPE_TYPE_SHORT: Record<RecipeType, string> = {
  main: 'Main',
  side: 'Side',
  appetizer: 'Appetizer',
  dessert: 'Dessert',
  beverage: 'Beverage',
  sauce: 'Sauce',
  breakfast: 'Breakfast',
  snack: 'Snack',
  other: 'Other',
};

export function recipeTypeShort(type: RecipeType): string {
  return RECIPE_TYPE_SHORT[type] ?? type;
}

/** Chip accent color per type (mirrors RecipeCard.razor GetTypeColor). */
export function recipeTypeColor(type: RecipeType): string {
  switch (type) {
    case 'dessert':
      return 'var(--color-secondary)';
    case 'beverage':
      return 'var(--color-info)';
    case 'breakfast':
      return 'var(--color-warning)';
    case 'appetizer':
      return 'var(--color-primary-soft)';
    default:
      return 'var(--color-text-muted)';
  }
}
