import { describe, expect, it } from 'vitest';
import { diffIngredients, diffRecipe, ingredientLine, isEmptyDiff, type DiffableForm } from './conflictDiff';
import type { RecipeFullDto, RecipeIngredientFullDto } from './types';

// Quest adfe3d57 part 2 (answer (c)): the field-level compare of the user's form against the latest saved
// version that the conflict banner shows before any keep-mine overwrite.

function ing(name: string, extra: Partial<RecipeIngredientFullDto> = {}): RecipeIngredientFullDto {
  return { ingredientId: 1, quantity: null, unit: null, name, category: 'Pantry', notes: null, groupName: null, sortOrder: 0, ...extra };
}

function latest(overrides: Partial<RecipeFullDto> = {}): RecipeFullDto {
  return {
    recipeId: 1,
    version: 9,
    name: 'Soup',
    recipeType: 'main',
    description: null,
    instructions: 'Simmer.',
    instructionsHtml: '',
    imagePath: null,
    sourceUrl: null,
    prepTimeMinutes: 10,
    cookTimeMinutes: null,
    servings: 4,
    createdByName: null,
    createdByPictureUrl: null,
    sharedFromHouseholdName: null,
    ingredients: [],
    ...overrides,
  };
}

function formOf(l: RecipeFullDto): DiffableForm {
  return {
    name: l.name,
    description: l.description ?? '',
    instructions: l.instructions ?? '',
    sourceUrl: l.sourceUrl ?? '',
    prepTimeMinutes: l.prepTimeMinutes,
    cookTimeMinutes: l.cookTimeMinutes,
    servings: l.servings,
    recipeType: l.recipeType,
    imagePath: l.imagePath,
  };
}

describe('diffRecipe', () => {
  it('identical content is an empty diff', () => {
    const l = latest();
    expect(isEmptyDiff(diffRecipe(formOf(l), [], l))).toBe(true);
  });

  it('whitespace and blank-vs-null never read as a change (normalized like the save body)', () => {
    const l = latest({ description: null, sourceUrl: null });
    const form = { ...formOf(l), name: '  Soup  ', description: '   ', instructions: 'Simmer.\n', sourceUrl: '' };
    expect(diffRecipe(form, [], l).fields).toEqual([]);
  });

  it('lists each changed field once, with yours and theirs as display strings', () => {
    const l = latest({ servings: 4, recipeType: 'side', prepTimeMinutes: null });
    const form = { ...formOf(l), servings: 6, recipeType: 'main' as const, prepTimeMinutes: 15 };

    expect(diffRecipe(form, [], l).fields).toEqual([
      { field: 'recipeType', label: 'Type', yours: 'main', theirs: 'side' },
      { field: 'servings', label: 'Servings', yours: '6', theirs: '4' },
      { field: 'prepTimeMinutes', label: 'Prep time (min)', yours: '15', theirs: '' },
    ]);
  });

  it('compares ingredients in the saved order (sortOrder), not the wire order', () => {
    const l = latest({ ingredients: [ing('salt', { sortOrder: 1 }), ing('water', { sortOrder: 0 })] });
    expect(diffRecipe(formOf(l), [ing('water'), ing('salt')], l).ingredients).toBeNull();
  });
});

describe('diffIngredients', () => {
  it('reports lines only in yours and only in theirs, as a multiset', () => {
    const diff = diffIngredients(
      [ing('salt'), ing('onion', { quantity: 2 }), ing('salt')],
      [ing('salt'), ing('onion', { quantity: 1 }), ing('leek', { notes: 'sliced', groupName: 'Base' })],
    );
    expect(diff).toEqual({
      onlyYours: ['2 onion', 'salt'],
      onlyTheirs: ['1 onion', 'leek, sliced [Base]'],
      reorderedOnly: false,
    });
  });

  it('the same lines in another order is a reorder, not a change of content', () => {
    expect(diffIngredients([ing('a'), ing('b')], [ing('b'), ing('a')])).toEqual({
      onlyYours: [],
      onlyTheirs: [],
      reorderedOnly: true,
    });
  });

  it('identical lists are no diff at all', () => {
    expect(diffIngredients([ing('a')], [ing('a')])).toBeNull();
  });

  it('a line names quantity, unit, name, notes and group, trimmed', () => {
    expect(ingredientLine({ quantity: 1.5, unit: ' cup ', name: ' rice ', notes: ' rinsed ', groupName: ' ' })).toBe('1.5 cup rice, rinsed');
  });
});
