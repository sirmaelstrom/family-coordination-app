import { beforeEach, describe, expect, it, vi } from 'vitest';

// The first rune-module test in the SPA (quest 5de6a2bd): recipeListStore is a `.svelte.ts`
// module whose fields are $state runes — importing it at all proves the vitest svelte-plugin
// wiring, and the cases below turn the PR #88 race contracts (until now enforced only by
// comments) into executable ones.

// The REAL ApiError (quest 41337b6f): the store's writes are classified by the Resource through the
// client's ApiError getters, so a test double class would read as a network failure.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
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
import { ApiError, listRecipes, toggleFavorite, deleteRecipe } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
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
    // Fixture change (41337b6f): the retired load is now OWED and re-runs once at settle, so the second
    // GET gets an explicit answer — the post-toggle server truth — instead of a mock returning undefined.
    vi.mocked(listRecipes).mockReturnValueOnce(inflight.promise).mockResolvedValueOnce(dto([], [1]));
    vi.mocked(toggleFavorite).mockResolvedValueOnce({ isFavorite: true });

    const loadPromise = recipeListStore.load();
    await recipeListStore.toggleFavorite(1);
    expect(recipeListStore.favoriteIds.has(1)).toBe(true);

    inflight.resolve(dto(['anything'], [])); // pre-toggle truth: not a favorite
    await loadPromise;

    expect(recipeListStore.favoriteIds.has(1)).toBe(true);
    expect(recipeListStore.recipes).toEqual([]);
    expect(listRecipes).toHaveBeenCalledTimes(2);
    expect(recipeListStore.error).toBeNull();
  });

  it('deleteRecipe retires an in-flight load so the card is not resurrected', async () => {
    recipeListStore.recipes = dto(['doomed']).recipes;
    const inflight = deferred<RecipeListDto>();
    // Fixture change (41337b6f): the owed re-load at settle answers with the post-delete truth.
    vi.mocked(listRecipes).mockReturnValueOnce(inflight.promise).mockResolvedValueOnce(dto([]));
    vi.mocked(deleteRecipe).mockResolvedValueOnce(undefined);

    const loadPromise = recipeListStore.load();
    await recipeListStore.deleteRecipe(1);
    expect(recipeListStore.recipes).toEqual([]);

    inflight.resolve(dto(['doomed'])); // the earlier load still carries the deleted recipe
    await loadPromise;

    expect(recipeListStore.recipes).toEqual([]);
    expect(listRecipes).toHaveBeenCalledTimes(2);
    expect(recipeListStore.error).toBeNull();
  });

  // ── 41337b6f: loads are serialized against pending writes ──────────────────────────────

  it('a load requested DURING a pending write is deferred, then runs once at settle (the fca#103 residual)', async () => {
    recipeListStore.recipes = dto(['doomed', 'kept']).recipes;
    const del = deferred<void>();
    vi.mocked(deleteRecipe).mockReturnValueOnce(del.promise);
    vi.mocked(listRecipes).mockResolvedValueOnce(dto(['kept']));

    const deleting = recipeListStore.deleteRecipe(1);
    // A liveness tick / search while the DELETE is pending: before, this GET was dispatched with a
    // current id and could answer with the pre-delete list before the DELETE's response.
    await recipeListStore.load();
    expect(listRecipes).not.toHaveBeenCalled();
    expect(recipeListStore.recipes.map((r) => r.name)).toEqual(['kept']);

    del.resolve();
    await deleting;
    expect(listRecipes).toHaveBeenCalledTimes(1);
    expect(recipeListStore.recipes.map((r) => r.name)).toEqual(['kept']);
  });

  it('a failed delete puts back only its own card — at its index — keeping what changed meanwhile', async () => {
    recipeListStore.recipes = dto(['a', 'b', 'c']).recipes; // ids 1, 2, 3
    const del = deferred<void>();
    vi.mocked(deleteRecipe).mockReturnValueOnce(del.promise).mockResolvedValueOnce(undefined);

    const failing = recipeListStore.deleteRecipe(2);
    await recipeListStore.deleteRecipe(3); // another write lands while the first is pending
    expect(recipeListStore.recipes.map((r) => r.name)).toEqual(['a']);

    del.reject(new TypeError('Failed to fetch')); // network: undo, no reconcile
    await failing;
    expect(recipeListStore.recipes.map((r) => r.name)).toEqual(['a', 'b']);
    expect(listRecipes).not.toHaveBeenCalled();
  });

  it('a 5xx favorite flips back with an error toast and no refetch; a 409 refetches', async () => {
    recipeListStore.favoriteIds = new Set();
    vi.mocked(toggleFavorite).mockRejectedValueOnce(new ApiError(503, 'down'));
    await recipeListStore.toggleFavorite(1);
    expect(recipeListStore.favoriteIds.has(1)).toBe(false);
    expect(listRecipes).not.toHaveBeenCalled();
    expect(showToast).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'error' }));

    vi.mocked(toggleFavorite).mockRejectedValueOnce(new ApiError(409, 'stale'));
    vi.mocked(listRecipes).mockResolvedValueOnce(dto(['x'], [1]));
    await recipeListStore.toggleFavorite(1);
    expect(listRecipes).toHaveBeenCalledTimes(1);
    expect(recipeListStore.favoriteIds.has(1)).toBe(true); // server truth after the reconcile
    expect(showToast).toHaveBeenLastCalledWith(expect.objectContaining({ kind: 'info' }));
  });

  it('a second click on the same heart while its POST is pending is skipped', async () => {
    const post = deferred<{ isFavorite: boolean }>();
    vi.mocked(toggleFavorite).mockReturnValueOnce(post.promise);
    const first = recipeListStore.toggleFavorite(1);
    await recipeListStore.toggleFavorite(1);
    expect(toggleFavorite).toHaveBeenCalledTimes(1);
    expect(recipeListStore.favoriteIds.has(1)).toBe(true);
    post.resolve({ isFavorite: true });
    await first;
    expect(recipeListStore.favoriteIds.has(1)).toBe(true);
  });

  it('the delete success toast shows only on success', async () => {
    recipeListStore.recipes = dto(['a']).recipes;
    vi.mocked(deleteRecipe).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    await recipeListStore.deleteRecipe(1);
    expect(showToast).not.toHaveBeenCalledWith(expect.objectContaining({ kind: 'success' }));
    vi.mocked(deleteRecipe).mockResolvedValueOnce(undefined);
    await recipeListStore.deleteRecipe(1);
    expect(showToast).toHaveBeenLastCalledWith({ message: 'Recipe deleted.', kind: 'success' });
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
