import { describe, expect, it } from 'vitest';
import { decodePreview, previewViewModel, toWriteRequest } from './importFlow';
import type { PartialRecipeDataDto, RecipeImportPreviewDto, RecipePreviewDto } from './types';

// The import-flow state machine (quest 1a8b49ea): the six-outcome decision ladder and the
// preview→WriteRequest mapping used to live as loose booleans + an inline literal in
// ImportDialog; these tests are the executable contract that extraction bought.

const fullPreview: RecipePreviewDto = {
  name: 'Pancakes',
  description: 'Fluffy',
  instructions: 'Mix and fry.',
  sourceUrl: 'https://example.test/pancakes',
  servings: 4,
  prepTimeMinutes: 10,
  cookTimeMinutes: null,
  recipeType: 'breakfast',
  imagePath: 'https://example.test/p.jpg',
  ingredients: [
    {
      name: 'flour',
      quantity: 2.5,
      unit: 'cup',
      category: 'Baking',
      notes: 'sifted',
      groupName: null,
      sortOrder: 1,
    },
  ],
};

function response(overrides: Partial<RecipeImportPreviewDto>): RecipeImportPreviewDto {
  return {
    success: false,
    recipe: null,
    errorMessage: null,
    errorType: null,
    existingRecipeId: null,
    existingRecipeName: null,
    partialData: null,
    ...overrides,
  };
}

const partialData: PartialRecipeDataDto = {
  name: 'Mystery Soup',
  description: null,
  instructions: 'Simmer.',
  imageUrl: null,
  servings: null,
  prepTimeMinutes: null,
  cookTimeMinutes: null,
  ingredientStrings: ['2 cups stock', 'salt'],
};

describe('decodePreview — the outcome ladder', () => {
  it('a successful parse is a preview outcome', () => {
    const outcome = decodePreview(response({ success: true, recipe: fullPreview }));
    expect(outcome).toEqual({ kind: 'preview', preview: fullPreview });
  });

  it('an existing recipe is a duplicate outcome carrying the id', () => {
    const outcome = decodePreview(
      response({ existingRecipeId: 42, errorMessage: 'Already imported as "Pancakes".' }),
    );
    expect(outcome).toEqual({
      kind: 'duplicate',
      existingRecipeId: 42,
      message: 'Already imported as "Pancakes".',
    });
  });

  it('a degraded parse is a partial outcome with a warning (failure honesty)', () => {
    const outcome = decodePreview(response({ partialData }));
    expect(outcome.kind).toBe('partial');
    if (outcome.kind === 'partial') {
      expect(outcome.partial).toBe(partialData);
      expect(outcome.warning).toMatch(/part of the recipe/i);
    }
  });

  it('a plain failure offers manual entry', () => {
    const outcome = decodePreview(response({ errorMessage: 'Site not supported.' }));
    expect(outcome).toEqual({ kind: 'error', message: 'Site not supported.', offerManual: true });
  });

  it('an invalid URL does NOT offer manual entry', () => {
    const outcome = decodePreview(response({ errorType: 'InvalidUrl', errorMessage: 'Bad URL.' }));
    expect(outcome).toEqual({ kind: 'error', message: 'Bad URL.', offerManual: false });
  });
});

describe('toWriteRequest — preview and confirm produce the same recipe', () => {
  it('a full preview posts VERBATIM plus version: null', () => {
    // THE contract this quest names: what the user previewed is byte-for-byte what confirm
    // creates — the only addition is the create-path version token.
    const body = toWriteRequest({ kind: 'preview', preview: fullPreview }, 'ignored');
    expect(body).toEqual({ ...fullPreview, version: null });
  });

  it('a partial maps its unparsed ingredient strings to name-only Pantry lines', () => {
    const body = toWriteRequest({ kind: 'partial', partial: partialData, warning: 'w' }, ' https://x.test ');
    expect(body).toMatchObject({
      name: 'Mystery Soup',
      recipeType: 'main',
      sourceUrl: 'https://x.test',
      version: null,
    });
    expect(body!.ingredients).toEqual([
      { name: '2 cups stock', quantity: null, unit: null, category: 'Pantry', notes: null, groupName: null, sortOrder: 0 },
      { name: 'salt', quantity: null, unit: null, category: 'Pantry', notes: null, groupName: null, sortOrder: 1 },
    ]);
  });

  it('a partial without a title is unconfirmable', () => {
    expect(toWriteRequest({ kind: 'partial', partial: { ...partialData, name: '  ' }, warning: 'w' }, 'x'))
      .toBeNull();
  });

  it('non-preview outcomes are unconfirmable', () => {
    expect(toWriteRequest({ kind: 'error', message: 'x', offerManual: true }, 'x')).toBeNull();
    expect(toWriteRequest({ kind: 'duplicate', existingRecipeId: 1, message: 'x' }, 'x')).toBeNull();
    expect(toWriteRequest(null, 'x')).toBeNull();
  });
});

describe('previewViewModel', () => {
  it('formats full-preview ingredient lines with quantity, unit and notes', () => {
    const vm = previewViewModel({ kind: 'preview', preview: fullPreview }, 'x');
    expect(vm!.ingredients).toEqual(['2 1/2 cup flour (sifted)']);
    expect(vm!.sourceUrl).toBe('https://example.test/pancakes');
  });

  it('a partial view model falls back to the typed URL and raw ingredient strings', () => {
    const vm = previewViewModel({ kind: 'partial', partial: partialData, warning: 'w' }, ' https://x.test ');
    expect(vm!.sourceUrl).toBe('https://x.test');
    expect(vm!.ingredients).toEqual(['2 cups stock', 'salt']);
  });

  it('url and error outcomes render nothing', () => {
    expect(previewViewModel(null, 'x')).toBeNull();
    expect(previewViewModel({ kind: 'error', message: 'x', offerManual: true }, 'x')).toBeNull();
  });
});
