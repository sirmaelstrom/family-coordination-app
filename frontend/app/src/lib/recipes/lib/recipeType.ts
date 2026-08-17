// The shared vocabulary surface lives in $lib/api/recipeType (quest ca10904a); this module
// re-exports it and keeps ONLY the presentation this island renders (short chip labels + colors).
import type { RecipeType } from './types';

export { RECIPE_TYPE_LABELS, RECIPE_TYPES, recipeTypeLabel } from '$lib/api/recipeType';

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
