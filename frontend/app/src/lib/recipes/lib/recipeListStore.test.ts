import { beforeEach, describe, expect, it, vi } from 'vitest';

// The first rune-module test in the SPA (quest 5de6a2bd): recipeListStore is a `.svelte.ts`
// module whose fields are $state runes — importing it at all proves the vitest svelte-plugin
// wiring, and the cases below turn the PR #88 race contracts (until now enforced only by
// comments) into executable ones.

vi.mock('./api', () => {
  class ApiError extends Error {
    constructor(public status: number) {
      super(`HTTP ${status}`);
    }
  }
  return {
    ApiError,
    listRecipes: vi.fn(),
    listConnectedRecipes: vi.fn(),
    toggleFavorite: vi.fn(),
    deleteRecipe: vi.fn(),
    getConnections: vi.fn(),
  };
});

vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { recipeListStore } from './recipeListStore.svelte';
import { listRecipes, toggleFavorite, deleteRecipe } from './api';
import type { RecipeListDto } from './types';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}

function dto(names: string[], favorites: number[] = []): RecipeListDto {
  return {
    recipes: names.map((name, i) => ({
      recipeId: i + 1,
      name,
      recipeType: 'main',
      imagePath: null,
      hasSourceUrl: false,
      createdByName: null,
      createdByPictureUrl: null,
      ingredientPreview: [],
      ingredientCount: 0,
    })),
    favoriteRecipeIds: favorites,
  };
}

describe('recipeListStore load-race contracts', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    // The store is a singleton; reset the state the tests read.
    recipeListStore.recipes = [];
    recipeListStore.favoriteIds = new Set();
    recipeListStore.selectedConnectedId = null;
    recipeListStore.query = '';
  });

  it('a superseded load must not overwrite the newer one', async () => {
    const slow = deferred<RecipeListDto>();
    const fast = deferred<RecipeListDto>();
    vi.mocked(listRecipes).mockReturnValueOnce(slow.promise).mockReturnValueOnce(fast.promise);

    const first = recipeListStore.load();
    const second = recipeListStore.load();

    fast.resolve(dto(['newer']));
    await second;
    slow.resolve(dto(['stale']));
    await first;

    expect(recipeListStore.recipes.map((r) => r.name)).toEqual(['newer']);
  });

  it('toggleFavorite retires an in-flight load before the optimistic write', async () => {
    // The PR #88 contract, verbatim from the store's own comment: a load that started earlier
    // still holds the pre-toggle favorites and must not land afterwards and flip the heart back.
    const inflight = deferred<RecipeListDto>();
    vi.mocked(listRecipes).mockReturnValueOnce(inflight.promise);
    vi.mocked(toggleFavorite).mockResolvedValueOnce({ isFavorite: true });

    const loadPromise = recipeListStore.load();
    await recipeListStore.toggleFavorite(1);
    expect(recipeListStore.favoriteIds.has(1)).toBe(true);

    inflight.resolve(dto(['anything'], [])); // pre-toggle truth: not a favorite
    await loadPromise;

    expect(recipeListStore.favoriteIds.has(1)).toBe(true);
    expect(recipeListStore.recipes).toEqual([]);
  });

  it('deleteRecipe retires an in-flight load so the card is not resurrected', async () => {
    recipeListStore.recipes = dto(['doomed']).recipes;
    const inflight = deferred<RecipeListDto>();
    vi.mocked(listRecipes).mockReturnValueOnce(inflight.promise);
    vi.mocked(deleteRecipe).mockResolvedValueOnce(undefined);

    const loadPromise = recipeListStore.load();
    await recipeListStore.deleteRecipe(1);
    expect(recipeListStore.recipes).toEqual([]);

    inflight.resolve(dto(['doomed'])); // the earlier load still carries the deleted recipe
    await loadPromise;

    expect(recipeListStore.recipes).toEqual([]);
  });

  it('a superseded load must not surface its error either', async () => {
    const slow = deferred<RecipeListDto>();
    const fast = deferred<RecipeListDto>();
    vi.mocked(listRecipes).mockReturnValueOnce(slow.promise).mockReturnValueOnce(fast.promise);

    const first = recipeListStore.load();
    const second = recipeListStore.load();

    fast.resolve(dto(['fine']));
    await second;
    slow.reject(new Error('network down'));
    await first;

    expect(recipeListStore.error).toBeNull();
    expect(recipeListStore.recipes.map((r) => r.name)).toEqual(['fine']);
  });
});
