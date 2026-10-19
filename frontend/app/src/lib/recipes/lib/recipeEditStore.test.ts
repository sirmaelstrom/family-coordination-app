import { afterEach, beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 0c97a292: a recipe draft stores the xmin token its edits started from (baseVersion), and a restored
// draft saves with THAT token, not the live one, so an edit someone saved after the draft began answers 409
// (the reload banner) instead of being overwritten.

// The REAL ApiError (quest 41337b6f): the store branches on its getters (isConflict / isClientRejection),
// which a stand-in class would not have. Fixture change only — every assertion below is unchanged.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    getRecipe: vi.fn(),
    getDraft: vi.fn(),
    saveDraft: vi.fn(),
    deleteDraft: vi.fn(),
    createRecipe: vi.fn(),
    updateRecipe: vi.fn(),
    deleteRecipe: vi.fn(),
    uploadImage: vi.fn(),
    getCategories: vi.fn(),
  };
});

vi.mock('$app/paths', () => ({ base: '' }));
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { recipeEditStore } from './recipeEditStore.svelte';
import { ApiError, deleteDraft, getCategories, getDraft, getRecipe, saveDraft, updateRecipe } from './api';
import type { RecipeDraftData, RecipeFullDto } from './types';
import { showToast } from '$lib/shared/toast-store.svelte';

const RECIPE_ID = 12;

function draft(baseVersion: number | null): RecipeDraftData {
  return {
    name: 'Soup (draft)',
    description: null,
    instructions: null,
    imagePath: null,
    sourceUrl: null,
    servings: 2,
    prepTimeMinutes: null,
    cookTimeMinutes: null,
    ingredients: [],
    baseVersion,
  };
}

function full(version: number): RecipeFullDto {
  return {
    recipeId: RECIPE_ID,
    version,
    name: 'Soup',
    recipeType: 'main',
    description: null,
    instructions: null,
    instructionsHtml: '',
    imagePath: null,
    sourceUrl: null,
    prepTimeMinutes: null,
    cookTimeMinutes: null,
    servings: 2,
    createdByName: null,
    createdByPictureUrl: null,
    sharedFromHouseholdName: null,
    ingredients: [],
  };
}

describe('recipe draft base version (0c97a292)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getCategories).mockResolvedValue([]);
    recipeEditStore.init({ userId: 1, recipeId: RECIPE_ID } as never);
  });

  afterEach(() => {
    recipeEditStore.teardown();
    vi.useRealTimers();
  });

  it('a restored draft saves with its own base version, not the live one', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(getRecipe).mockResolvedValue(full(5)); // someone saved v5 after the draft began on v3

    await recipeEditStore.load(RECIPE_ID);
    // A 409 keeps the store on the page (a success would navigate, which needs a window).
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));
    await recipeEditStore.save();

    expect(updateRecipe).toHaveBeenCalledTimes(1);
    expect(vi.mocked(updateRecipe).mock.calls[0][1].version).toBe(3);
    // No GET before the save: the token is the draft's base, never a live fetch. The 409 itself now fetches the
    // latest version once, AFTER the PUT, for the conflict diff (quest adfe3d57); that call is the only one.
    const putAt = vi.mocked(updateRecipe).mock.invocationCallOrder[0];
    expect(vi.mocked(getRecipe).mock.invocationCallOrder.filter((at) => at < putAt)).toEqual([]);
    expect(getRecipe).toHaveBeenCalledTimes(1);
  });

  it('a 409 on the restored draft\'s save shows the reload banner (the server half is RecipesEndpointTests)', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));

    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();

    expect(recipeEditStore.conflict).toBe(true);
  });

  it('a legacy draft with no base version falls back to the live token', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(null));
    vi.mocked(getRecipe).mockResolvedValue(full(5));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));

    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();

    expect(getRecipe).toHaveBeenCalledWith(RECIPE_ID);
    expect(vi.mocked(updateRecipe).mock.calls[0][1].version).toBe(5);
  });

  it('the autosaved draft carries the version the editor loaded', async () => {
    vi.useFakeTimers();
    vi.mocked(getDraft).mockResolvedValue(null);
    vi.mocked(getRecipe).mockResolvedValue(full(5));
    vi.mocked(saveDraft).mockResolvedValue(undefined);

    await recipeEditStore.load(RECIPE_ID);
    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000);

    expect(saveDraft).toHaveBeenCalledTimes(1);
    expect(vi.mocked(saveDraft).mock.calls[0][0]).toMatchObject({ recipeId: RECIPE_ID, baseVersion: 5 });
  });

  it('a restored draft keeps its base through autosave', async () => {
    vi.useFakeTimers();
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(saveDraft).mockResolvedValue(undefined);

    await recipeEditStore.load(RECIPE_ID);
    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000);

    expect(vi.mocked(saveDraft).mock.calls[0][0]).toMatchObject({ recipeId: RECIPE_ID, baseVersion: 3 });
  });

  it('a conflict reload keeps the old base on the saved draft, then re-bases on the next edit', async () => {
    vi.useFakeTimers();
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));
    vi.mocked(saveDraft).mockResolvedValue(undefined);

    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();
    expect(recipeEditStore.conflict).toBe(true);

    vi.mocked(getRecipe).mockResolvedValue(full(5));
    await recipeEditStore.reloadAfterConflict();

    // The typed state is preserved under the base it was made on, not the reloaded one.
    expect(vi.mocked(saveDraft).mock.calls[0][0]).toMatchObject({ name: 'Soup (draft)', baseVersion: 3 });
    expect(recipeEditStore.conflict).toBe(false);
    expect(recipeEditStore.form.name).toBe('Soup');

    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000);
    expect(vi.mocked(saveDraft).mock.calls[1][0]).toMatchObject({ name: 'Soup', baseVersion: 5 });
  });

  it('a new-recipe draft carries no base version', async () => {
    vi.useFakeTimers();
    recipeEditStore.init({ userId: 1, recipeId: null } as never);
    vi.mocked(getDraft).mockResolvedValue(null);
    vi.mocked(saveDraft).mockResolvedValue(undefined);

    await recipeEditStore.load(null);
    recipeEditStore.form.name = 'New soup';
    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000);

    expect(vi.mocked(saveDraft).mock.calls[0][0]).toMatchObject({ recipeId: null, baseVersion: null });
  });
});

// Quest adfe3d57: a draft for a recipe deleted meanwhile is removed at the 404 instead of reopening on every
// visit, and the editor has an explicit Discard-draft control.
describe('recipe draft recovery (adfe3d57)', () => {
  const assign = vi.fn();

  beforeEach(() => {
    vi.clearAllMocks();
    vi.stubGlobal('window', { location: { assign } });
    vi.mocked(getCategories).mockResolvedValue([]);
    vi.mocked(deleteDraft).mockResolvedValue(undefined);
    vi.mocked(saveDraft).mockResolvedValue(undefined);
    recipeEditStore.init({ userId: 1, recipeId: RECIPE_ID } as never);
  });

  afterEach(() => {
    recipeEditStore.teardown();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  it('a 404 on save deletes the orphan draft, then leaves for the list', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(404, 'gone'));

    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();

    expect(deleteDraft).toHaveBeenCalledWith(RECIPE_ID);
    expect(vi.mocked(deleteDraft).mock.invocationCallOrder[0]).toBeLessThan(assign.mock.invocationCallOrder[0]);
    expect(assign).toHaveBeenCalledWith('/recipes');
  });

  it('a 400 on save keeps the draft (only a missing recipe makes it an orphan)', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(400, 'bad'));

    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();

    expect(deleteDraft).not.toHaveBeenCalled();
  });

  it('a 5xx on save keeps the form and the draft, with an error toast (not "the recipe changed")', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(503, 'unavailable'));

    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();

    expect(assign).not.toHaveBeenCalled();
    expect(deleteDraft).not.toHaveBeenCalled();
    expect(recipeEditStore.conflict).toBe(false);
    expect(recipeEditStore.error).toBeNull();
    expect(showToast).toHaveBeenLastCalledWith({ message: 'Something went wrong saving. Please try again.', kind: 'error' });
  });

  it('a 404 on the conflict reload deletes the draft it just saved', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));
    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();

    vi.mocked(getRecipe).mockRejectedValue(new ApiError(404, 'gone'));
    await recipeEditStore.reloadAfterConflict();

    expect(saveDraft).toHaveBeenCalledTimes(1);
    expect(deleteDraft).toHaveBeenCalledWith(RECIPE_ID);
    expect(vi.mocked(saveDraft).mock.invocationCallOrder[0]).toBeLessThan(
      vi.mocked(deleteDraft).mock.invocationCallOrder[0],
    );
    expect(assign).toHaveBeenCalledWith('/recipes');
  });

  it('hasDraft follows a restored draft', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    await recipeEditStore.load(RECIPE_ID);
    expect(recipeEditStore.hasDraft).toBe(true);

    vi.mocked(getDraft).mockResolvedValue(null);
    vi.mocked(getRecipe).mockResolvedValue(full(5));
    await recipeEditStore.load(RECIPE_ID);
    expect(recipeEditStore.hasDraft).toBe(false);
  });

  it('an autosave sets hasDraft', async () => {
    vi.useFakeTimers();
    vi.mocked(getDraft).mockResolvedValue(null);
    vi.mocked(getRecipe).mockResolvedValue(full(5));
    await recipeEditStore.load(RECIPE_ID);

    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000);

    expect(recipeEditStore.hasDraft).toBe(true);
  });

  it('discard deletes the draft and shows the latest saved recipe, clearing the conflict', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));
    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();
    expect(recipeEditStore.conflict).toBe(true);

    vi.mocked(getRecipe).mockResolvedValue(full(5));
    await recipeEditStore.discardDraft();

    expect(deleteDraft).toHaveBeenCalledWith(RECIPE_ID);
    expect(recipeEditStore.form.name).toBe('Soup');
    expect(recipeEditStore.conflict).toBe(false);
    expect(recipeEditStore.hasDraft).toBe(false);
    expect(recipeEditStore.dirty).toBe(false);

    // The next save is based on the reloaded version, not the discarded draft's.
    await recipeEditStore.save();
    expect(vi.mocked(updateRecipe).mock.calls[1][1].version).toBe(5);
  });

  it('a failed discard keeps the form and the draft', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    await recipeEditStore.load(RECIPE_ID);
    vi.mocked(deleteDraft).mockRejectedValue(new Error('network'));

    await recipeEditStore.discardDraft();

    expect(recipeEditStore.form.name).toBe('Soup (draft)');
    expect(recipeEditStore.hasDraft).toBe(true);
    expect(getRecipe).not.toHaveBeenCalled();
  });

  it('discard on a new recipe resets to a blank form', async () => {
    recipeEditStore.init({ userId: 1, recipeId: null } as never);
    vi.mocked(getDraft).mockResolvedValue(draft(null));
    await recipeEditStore.load(null);

    await recipeEditStore.discardDraft();

    expect(deleteDraft).toHaveBeenCalledWith(null);
    expect(recipeEditStore.form.name).toBe('');
    expect(recipeEditStore.ingredients).toEqual([]);
    expect(getRecipe).not.toHaveBeenCalled();
  });

  it('discard does nothing while a conflict reload is saving its draft, and reload does nothing during a discard', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));
    await recipeEditStore.load(RECIPE_ID);
    await recipeEditStore.save();

    // Reload's own draft PUT is pending: a discard now would DELETE before that PUT lands.
    let releaseSave!: () => void;
    vi.mocked(saveDraft).mockReturnValue(new Promise<void>((r) => (releaseSave = r)));
    vi.mocked(getRecipe).mockResolvedValue(full(5));
    const reloading = recipeEditStore.reloadAfterConflict();
    try {
      await recipeEditStore.discardDraft();
      expect(deleteDraft).not.toHaveBeenCalled();
    } finally {
      releaseSave(); // settle the store even on failure, so its flags don't leak into later tests
      await reloading;
    }

    // The reverse: a discard whose DELETE is pending blocks a reload (its saveDraft would re-create the draft).
    vi.mocked(saveDraft).mockClear();
    vi.mocked(updateRecipe).mockRejectedValue(new ApiError(409, 'conflict'));
    await recipeEditStore.save();
    let releaseDelete!: () => void;
    vi.mocked(deleteDraft).mockReturnValue(new Promise<void>((r) => (releaseDelete = r)));
    const discarding = recipeEditStore.discardDraft();
    try {
      await Promise.resolve();
      await recipeEditStore.reloadAfterConflict();
      expect(saveDraft).not.toHaveBeenCalled();
    } finally {
      releaseDelete();
      await discarding;
    }
  });

  it('a non-404 failure loading the recipe after a discard stays on the page', async () => {
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    await recipeEditStore.load(RECIPE_ID);
    vi.mocked(getRecipe).mockRejectedValue(new ApiError(500, 'boom'));

    await recipeEditStore.discardDraft();

    expect(deleteDraft).toHaveBeenCalledWith(RECIPE_ID);
    expect(assign).not.toHaveBeenCalled();
    expect(recipeEditStore.error).toMatch(/couldn't be loaded/);
  });

  it('discard waits out EVERY in-flight autosave, not only the latest', async () => {
    vi.useFakeTimers();
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    await recipeEditStore.load(RECIPE_ID);

    let releaseSlow!: () => void;
    vi.mocked(saveDraft).mockReturnValueOnce(new Promise<void>((r) => (releaseSlow = r)));
    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000); // slow PUT #1 on the wire
    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000); // PUT #2 (default mock) resolves at once
    expect(saveDraft).toHaveBeenCalledTimes(2);

    vi.mocked(getRecipe).mockResolvedValue(full(5));
    const discarding = recipeEditStore.discardDraft();
    try {
      await vi.advanceTimersByTimeAsync(0);
      expect(deleteDraft).not.toHaveBeenCalled(); // PUT #1 could still land after a DELETE
    } finally {
      releaseSlow();
      await discarding;
    }
    expect(deleteDraft).toHaveBeenCalledTimes(1);
  });

  it('discard waits out an in-flight autosave so it cannot re-create the draft', async () => {
    vi.useFakeTimers();
    vi.mocked(getDraft).mockResolvedValue(draft(3));
    await recipeEditStore.load(RECIPE_ID);

    let release!: () => void;
    vi.mocked(saveDraft).mockReturnValue(new Promise<void>((r) => (release = r)));
    recipeEditStore.onEdit();
    await vi.advanceTimersByTimeAsync(2000); // the autosave PUT is now in flight
    expect(saveDraft).toHaveBeenCalledTimes(1);

    vi.mocked(getRecipe).mockResolvedValue(full(5));
    const discarding = recipeEditStore.discardDraft();
    await vi.advanceTimersByTimeAsync(0);
    expect(deleteDraft).not.toHaveBeenCalled();

    release();
    await discarding;
    expect(deleteDraft).toHaveBeenCalledTimes(1);
    expect(recipeEditStore.hasDraft).toBe(false);
  });
});

// Quest adfe3d57 part 2, operator answer (c): after a 409, show a field-level diff of mine vs the latest version
// first, then let the user keep mine (an explicit overwrite with THAT version's token) or take theirs.
describe('recipe conflict diff, then keep mine or take theirs (adfe3d57)', () => {
  const assign = vi.fn();

  beforeEach(async () => {
    vi.clearAllMocks();
    vi.stubGlobal('window', { location: { assign } });
    vi.mocked(getCategories).mockResolvedValue([]);
    vi.mocked(deleteDraft).mockResolvedValue(undefined);
    vi.mocked(saveDraft).mockResolvedValue(undefined);
    recipeEditStore.init({ userId: 1, recipeId: RECIPE_ID } as never);
    vi.mocked(getDraft).mockResolvedValue(null);
    vi.mocked(getRecipe).mockResolvedValueOnce(full(5));
    await recipeEditStore.load(RECIPE_ID);
    recipeEditStore.form.name = 'Soup, mine';
    recipeEditStore.form.servings = 6;
    recipeEditStore.onEdit();
  });

  afterEach(() => {
    recipeEditStore.teardown();
    vi.unstubAllGlobals();
    vi.useRealTimers();
  });

  function theirs(version: number): RecipeFullDto {
    return { ...full(version), name: 'Soup, theirs', description: 'Now with leeks' };
  }

  it('a 409 fetches the latest version and shows exactly the fields that differ', async () => {
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict'));
    vi.mocked(getRecipe).mockResolvedValueOnce(theirs(7));

    await recipeEditStore.save();

    expect(recipeEditStore.conflict).toBe(true);
    expect(recipeEditStore.conflictDiff?.fields).toEqual([
      { field: 'name', label: 'Name', yours: 'Soup, mine', theirs: 'Soup, theirs' },
      { field: 'description', label: 'Description', yours: '', theirs: 'Now with leeks' },
      { field: 'servings', label: 'Servings', yours: '6', theirs: '2' },
    ]);
    expect(recipeEditStore.conflictDiff?.ingredients).toBeNull();
  });

  it('keep mine saves over exactly the version the diff showed', async () => {
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict'));
    vi.mocked(getRecipe).mockResolvedValueOnce(theirs(7));
    await recipeEditStore.save();
    vi.mocked(updateRecipe).mockResolvedValueOnce(theirs(8) as never);

    await recipeEditStore.keepMine();

    expect(updateRecipe).toHaveBeenCalledTimes(2);
    expect(vi.mocked(updateRecipe).mock.calls[0][1].version).toBe(5); // the stale base that conflicted
    expect(vi.mocked(updateRecipe).mock.calls[1][1]).toMatchObject({ version: 7, name: 'Soup, mine', servings: 6 });
    expect(deleteDraft).toHaveBeenCalled();
    expect(assign).toHaveBeenCalledWith('/recipes');
  });

  it('a save landing after the diff makes keep mine conflict again and refresh the diff; nothing unseen is overwritten', async () => {
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict'));
    vi.mocked(getRecipe).mockResolvedValueOnce(theirs(7));
    await recipeEditStore.save();
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict')); // v8 landed after the diff
    vi.mocked(getRecipe).mockResolvedValueOnce({ ...theirs(8), servings: 10 });

    await recipeEditStore.keepMine();

    expect(vi.mocked(updateRecipe).mock.calls[1][1].version).toBe(7);
    expect(recipeEditStore.conflict).toBe(true);
    expect(recipeEditStore.conflictDiff?.fields.find((f) => f.field === 'servings')?.theirs).toBe('10');
    expect(assign).not.toHaveBeenCalled();

    vi.mocked(updateRecipe).mockResolvedValueOnce(theirs(9) as never);
    await recipeEditStore.keepMine();
    expect(vi.mocked(updateRecipe).mock.calls[2][1].version).toBe(8);
  });

  it('no diff, no keep mine: when the latest version cannot be fetched, keep mine does nothing', async () => {
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict'));
    vi.mocked(getRecipe).mockRejectedValueOnce(new ApiError(503, 'down'));
    await recipeEditStore.save();

    expect(recipeEditStore.conflict).toBe(true);
    expect(recipeEditStore.conflictDiff).toBeNull();
    expect(recipeEditStore.conflictGone).toBe(false);

    await recipeEditStore.keepMine();
    expect(updateRecipe).toHaveBeenCalledTimes(1);
  });

  it('a 404 for the latest version says the recipe is gone', async () => {
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict'));
    vi.mocked(getRecipe).mockRejectedValueOnce(new ApiError(404, 'gone'));

    await recipeEditStore.save();

    expect(recipeEditStore.conflictDiff).toBeNull();
    expect(recipeEditStore.conflictGone).toBe(true);
  });

  it('the diff follows the form while the user keeps typing', async () => {
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict'));
    vi.mocked(getRecipe).mockResolvedValueOnce(theirs(7));
    await recipeEditStore.save();

    recipeEditStore.form.name = 'Soup, theirs';

    expect(recipeEditStore.conflictDiff?.fields.map((f) => f.field)).toEqual(['description', 'servings']);
  });

  it('take theirs is the existing reload, and it clears the diff', async () => {
    vi.mocked(updateRecipe).mockRejectedValueOnce(new ApiError(409, 'conflict'));
    vi.mocked(getRecipe).mockResolvedValueOnce(theirs(7));
    await recipeEditStore.save();
    vi.mocked(getRecipe).mockResolvedValueOnce(theirs(7));

    await recipeEditStore.reloadAfterConflict();

    expect(recipeEditStore.conflict).toBe(false);
    expect(recipeEditStore.conflictDiff).toBeNull();
    expect(recipeEditStore.form.name).toBe('Soup, theirs');
    expect(vi.mocked(saveDraft).mock.calls.at(-1)?.[0]).toMatchObject({ name: 'Soup, mine' }); // mine kept as a draft
  });
});
