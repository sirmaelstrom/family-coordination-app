import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f WP-3: meal-plan writes go through the Resource. Before it, NO meal-plan mutation retired
// an in-flight load, so a same-week liveness/reconcile GET could land after an optimistic move/remove and
// revert it. These tests are written against the store's public surface so they also run (red) against
// origin/master's store — the controls for the pre-existing bug.

vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    getBoard: vi.fn(),
    addEntry: vi.fn(),
    moveEntry: vi.fn(),
    setEntryServings: vi.fn(),
    removeEntry: vi.fn(),
  };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { mealPlanStore as store } from './state.svelte';
import { ApiError, getBoard, moveEntry, removeEntry } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { MealPlanBoardDto, MealPlanEntryDto } from './types';

const W1 = '2026-10-12';
const W2 = '2026-10-19';

function entry(entryId: number, date = W1, mealType: MealPlanEntryDto['mealType'] = 'dinner'): MealPlanEntryDto {
  return {
    mealPlanId: 1,
    entryId,
    date,
    mealType,
    recipe: null,
    customMealName: `meal ${entryId}`,
    notes: null,
    servings: null,
    version: 10 + entryId,
  };
}
function board(week: string, entries: MealPlanEntryDto[]): MealPlanBoardDto {
  return { weekStartDate: week, mealPlanId: 1, entries };
}
function deferred<V>() {
  let resolve!: (v: V) => void;
  let reject!: (e: unknown) => void;
  const promise = new Promise<V>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
const ids = () => (store.board?.entries ?? []).map((e) => e.entryId);

describe('meal-plan writes vs loads (41337b6f)', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    vi.mocked(getBoard).mockReset();
    store.weekStart = W1;
    store.board = board(W1, [entry(1), entry(2)]);
    store.error = null;
  });

  it('S3: a same-week load requested while a remove is pending cannot resurrect the entry', async () => {
    const del = deferred<void>();
    vi.mocked(removeEntry).mockReturnValueOnce(del.promise);
    // A faithful server: until the DELETE has landed, a GET answers with the pre-delete board.
    let deleted = false;
    vi.mocked(getBoard).mockImplementation(async () =>
      board(W1, deleted ? [entry(2)] : [entry(1), entry(2)]),
    );

    const removing = store.removeEntry(1, 1, 11);
    expect(ids()).toEqual([2]);

    await store.loadBoard(); // a liveness tick while the DELETE is pending
    expect(ids()).toEqual([2]);

    deleted = true;
    del.resolve();
    await removing;
    expect(ids()).toEqual([2]);
    expect(getBoard).toHaveBeenCalledTimes(1); // the deferred tick, run once at settle
  });

  it('S3: a load already in flight when a move starts cannot revert the move', async () => {
    const inflight = deferred<MealPlanBoardDto>();
    vi.mocked(getBoard)
      .mockReturnValueOnce(inflight.promise)
      .mockResolvedValueOnce(board(W1, [{ ...entry(1), mealType: 'lunch' }, entry(2)]));
    const patch = deferred<MealPlanEntryDto>();
    vi.mocked(moveEntry).mockReturnValueOnce(patch.promise);

    const loading = store.loadBoard();
    const moving = store.moveEntry(1, 1, W1, 'lunch', 11);
    expect(store.board!.entries.find((e) => e.entryId === 1)!.mealType).toBe('lunch');

    inflight.resolve(board(W1, [entry(1), entry(2)])); // pre-move truth: still dinner
    await loading;
    expect(store.board!.entries.find((e) => e.entryId === 1)!.mealType).toBe('lunch');

    patch.resolve({ ...entry(1), mealType: 'lunch', version: 99 });
    await moving;
    expect(store.board!.entries.find((e) => e.entryId === 1)!.mealType).toBe('lunch');
  });

  it('S4: a failed remove after a week change splices nothing into the new week', async () => {
    const del = deferred<void>();
    vi.mocked(removeEntry).mockReturnValueOnce(del.promise);
    const nextWeek = deferred<MealPlanBoardDto>();
    vi.mocked(getBoard).mockReturnValueOnce(nextWeek.promise);

    const removing = store.removeEntry(1, 1, 11);
    const stepping = store.changeWeek(1);
    del.reject(new TypeError('Failed to fetch'));
    await new Promise((r) => setTimeout(r, 0)); // the undo has run; the new week's GET is pending
    expect(store.weekStart).toBe(W2);
    expect(ids()).toEqual([2]); // nothing spliced back while the user looks at another week

    nextWeek.resolve(board(W2, [entry(9, W2)]));
    await removing;
    await stepping;
    expect(ids()).toEqual([9]);
    expect(showToast).not.toHaveBeenCalled(); // a failure on a week the user has left is moot
  });

  it('a week change during a pending write loads the NEW week once, after the write settles', async () => {
    const del = deferred<void>();
    vi.mocked(removeEntry).mockReturnValueOnce(del.promise);
    vi.mocked(getBoard).mockResolvedValue(board(W2, [entry(9, W2)]));

    const removing = store.removeEntry(1, 1, 11);
    await store.changeWeek(1);
    expect(getBoard).not.toHaveBeenCalled(); // deferred behind the DELETE
    expect(store.weekStart).toBe(W2);

    del.resolve();
    await removing;
    expect(getBoard).toHaveBeenCalledTimes(1);
    expect(getBoard).toHaveBeenCalledWith(W2);
    expect(ids()).toEqual([9]);
  });

  it('a second action on an entry whose write is pending is skipped (no stale-token request)', async () => {
    const patch = deferred<MealPlanEntryDto>();
    vi.mocked(moveEntry).mockReturnValueOnce(patch.promise);
    const moving = store.moveEntry(1, 1, W1, 'lunch', 11);

    await store.removeEntry(1, 1, 11);
    expect(removeEntry).not.toHaveBeenCalled();
    expect(ids()).toEqual([1, 2]);

    patch.resolve({ ...entry(1), mealType: 'lunch', version: 99 });
    await moving;
  });

  it('409: puts the entry back, reconciles, and says the meal changed (copy chosen by outcome)', async () => {
    vi.mocked(removeEntry).mockRejectedValueOnce(new ApiError(409, 'stale'));
    vi.mocked(getBoard).mockResolvedValueOnce(board(W1, [{ ...entry(1), version: 50 }, entry(2)]));

    await store.removeEntry(1, 1, 11);

    expect(getBoard).toHaveBeenCalledTimes(1);
    expect(store.board!.entries.find((e) => e.entryId === 1)!.version).toBe(50);
    expect(showToast).toHaveBeenCalledWith({
      message: 'That meal just changed — refreshing the plan. Try again in a moment.',
      kind: 'info',
    });
  });

  it('5xx: puts the entry back at its index with an error toast and no reconcile', async () => {
    vi.mocked(removeEntry).mockRejectedValueOnce(new ApiError(503, 'down'));

    await store.removeEntry(1, 1, 11);

    expect(ids()).toEqual([1, 2]);
    expect(getBoard).not.toHaveBeenCalled();
    expect(showToast).toHaveBeenCalledWith({ message: 'Something went wrong. Please try again.', kind: 'error' });
  });

  it('the optimistic move is applied synchronously (zoneFinalize releases the drag gate right after)', async () => {
    const patch = deferred<MealPlanEntryDto>();
    vi.mocked(moveEntry).mockReturnValueOnce(patch.promise);
    const moving = store.moveEntry(1, 2, W1, 'breakfast', 12);
    expect(store.board!.entries.find((e) => e.entryId === 2)!.mealType).toBe('breakfast');
    // Settle it: the store is a singleton, and an open write would defer every later test's loads.
    patch.resolve({ ...entry(2), mealType: 'breakfast', version: 99 });
    await moving;
  });

  it('a second drag of an entry whose move is pending is skipped and the zones re-derive from the board', async () => {
    const patch = deferred<MealPlanEntryDto>();
    vi.mocked(moveEntry).mockReturnValueOnce(patch.promise);
    store.rebuildZones();
    const moving = store.moveEntry(1, 1, W1, 'lunch', 11); // first drag: dinner → lunch, pending
    store.rebuildZones(); // what App's effect does once the first drag's gate is released

    // Second drag of the same card: lunch → breakfast, dropped into the breakfast zone.
    const card = store.zoneFor(W1, 'lunch').find((e) => e.entryId === 1)!;
    await store.zoneFinalize(W1, 'breakfast', [card]);

    expect(moveEntry).toHaveBeenCalledTimes(1); // no second request with the same stale token
    expect(store.board!.entries.find((e) => e.entryId === 1)!.mealType).toBe('lunch');
    expect(store.zoneFor(W1, 'breakfast').some((e) => e.entryId === 1)).toBe(false);
    expect(store.zoneFor(W1, 'lunch').some((e) => e.entryId === 1)).toBe(true);

    patch.resolve({ ...entry(1), mealType: 'lunch', version: 99 });
    await moving;
  });
});
