import { beforeEach, describe, expect, it, vi } from 'vitest';

// The Deleted Recipes store on the Resource: restore is an optimistic mutate(). The row leaves at once;
// a 5xx puts it back with no reload, a 409/4xx puts it back and reloads, and a second Restore on a
// pending row is dropped.

// The REAL ApiError: the Resource classifies failures through its getters.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('#lib/api/client.ts')>('#lib/api/client.ts');
  return { ApiError, getDeletedRecipes: vi.fn(), restoreRecipe: vi.fn() };
});
vi.mock('#lib/shared/toast-store.svelte.ts', () => ({ showToast: vi.fn() }));

import { deletedRecipesStore } from './deletedRecipesStore.svelte';
import { ApiError, getDeletedRecipes, restoreRecipe } from './api';
import { showToast } from '#lib/shared/toast-store.svelte.ts';
import type { DeletedRecipeDto, DeletedRecipeListDto } from './types';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
/** Let the settle load (dispatched, not awaited, by the write) run to completion. */
const flush = () => new Promise<void>((r) => setTimeout(r, 0));

const recipe = (recipeId: number, name: string): DeletedRecipeDto => ({
  recipeId,
  name,
  recipeType: 'main',
  deletedAt: '2026-06-20T14:30:00Z',
});
const CHILI = recipe(1, 'Old Chili');
const SOUP = recipe(2, 'Binned Soup');
const TART = recipe(3, 'Lost Tart');
const list = (...recipes: DeletedRecipeDto[]): DeletedRecipeListDto => ({ recipes });
const names = () => deletedRecipesStore.recipes.map((r) => r.name);

describe('deletedRecipesStore', () => {
  beforeEach(async () => {
    vi.resetAllMocks(); // also drops unconsumed *Once answers
    vi.mocked(getDeletedRecipes).mockResolvedValueOnce(list(CHILI, SOUP, TART));
    await deletedRecipesStore.load();
    await flush();
    vi.resetAllMocks();
  });

  it('load commits the server order and clears the first-load spinner', () => {
    expect(names()).toEqual(['Old Chili', 'Binned Soup', 'Lost Tart']);
    expect(deletedRecipesStore.loading).toBe(false);
    expect(deletedRecipesStore.error).toBeNull();
  });

  it('a failed load reports the error and keeps the rows it had', async () => {
    vi.mocked(getDeletedRecipes).mockRejectedValueOnce(new ApiError(503, 'down'));
    await deletedRecipesStore.load();
    expect(deletedRecipesStore.error).toBe('down');
    expect(names()).toEqual(['Old Chili', 'Binned Soup', 'Lost Tart']);
  });

  it('restore removes the row at once, calls the API with its id and toasts the name', async () => {
    const pending = deferred<void>();
    vi.mocked(restoreRecipe).mockReturnValueOnce(pending.promise);

    const restoring = deletedRecipesStore.restore(SOUP);
    expect(names()).toEqual(['Old Chili', 'Lost Tart']); // optimistic, synchronous
    expect(showToast).not.toHaveBeenCalled(); // nothing is claimed before the server answers

    pending.resolve();
    await restoring;
    await flush();

    expect(restoreRecipe).toHaveBeenCalledWith(2);
    expect(names()).toEqual(['Old Chili', 'Lost Tart']);
    expect(getDeletedRecipes).not.toHaveBeenCalled(); // a success needs no reload
    expect(showToast).toHaveBeenCalledWith({ message: '"Binned Soup" restored.', kind: 'success' });
  });

  it('a 5xx restore puts the row back where it was, with an error toast and no reload', async () => {
    vi.mocked(restoreRecipe).mockRejectedValueOnce(new ApiError(503, 'down'));

    await deletedRecipesStore.restore(SOUP);
    await flush();

    expect(names()).toEqual(['Old Chili', 'Binned Soup', 'Lost Tart']);
    expect(getDeletedRecipes).not.toHaveBeenCalled();
    expect(showToast).toHaveBeenCalledTimes(1);
    expect(showToast).toHaveBeenCalledWith({ message: "Couldn't restore that recipe right now.", kind: 'error' });
  });

  it('a 404 restore reloads the list, which is the truth (the recipe is gone from it)', async () => {
    vi.mocked(restoreRecipe).mockRejectedValueOnce(new ApiError(404, 'Recipe not found.'));
    vi.mocked(getDeletedRecipes).mockResolvedValueOnce(list(CHILI, TART));

    await deletedRecipesStore.restore(SOUP);
    await flush();

    expect(getDeletedRecipes).toHaveBeenCalledTimes(1);
    expect(names()).toEqual(['Old Chili', 'Lost Tart']);
    expect(showToast).toHaveBeenCalledTimes(1);
    expect(vi.mocked(showToast).mock.calls[0][0].message).toBe("Couldn't restore that recipe — refreshing the list.");
  });

  it('a 409 restore puts the row back and reloads, so it can be tried again', async () => {
    vi.mocked(restoreRecipe).mockRejectedValueOnce(new ApiError(409, 'stale'));
    vi.mocked(getDeletedRecipes).mockResolvedValueOnce(list(CHILI, SOUP, TART));

    await deletedRecipesStore.restore(SOUP);
    await flush();

    expect(getDeletedRecipes).toHaveBeenCalledTimes(1);
    expect(names()).toEqual(['Old Chili', 'Binned Soup', 'Lost Tart']);
    expect(vi.mocked(showToast).mock.calls[0][0].message).toBe('That recipe changed — refreshing the list. Try again.');
  });

  it('a second Restore on a row whose restore is pending sends nothing', async () => {
    const pending = deferred<void>();
    vi.mocked(restoreRecipe).mockReturnValueOnce(pending.promise);

    const first = deletedRecipesStore.restore(SOUP);
    await deletedRecipesStore.restore(SOUP);
    expect(restoreRecipe).toHaveBeenCalledTimes(1);

    pending.resolve();
    await first;
    await flush();
    expect(showToast).toHaveBeenCalledTimes(1);
  });

  it('a failed restore does not undo another restore that succeeded meanwhile', async () => {
    const soup = deferred<void>();
    vi.mocked(restoreRecipe).mockReturnValueOnce(soup.promise).mockResolvedValueOnce(undefined);

    const failing = deletedRecipesStore.restore(SOUP);
    await deletedRecipesStore.restore(TART);
    soup.reject(new ApiError(503, 'down'));
    await failing;
    await flush();

    // Only the soup comes back; the tart stays restored.
    expect(names()).toEqual(['Old Chili', 'Binned Soup']);
  });
});
