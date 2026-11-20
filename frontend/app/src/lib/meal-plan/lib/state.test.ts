import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f WP-3: meal-plan writes go through the Resource. Before it, NO meal-plan mutation retired
// an in-flight load, so a same-week liveness/reconcile GET could land after an optimistic move/remove and
// revert it. These tests are written against the store's public surface so they also run (red) against
// origin/master's store — the controls for the pre-existing bug.

vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('#lib/api/client.ts')>('#lib/api/client.ts');
  return {
    ApiError,
    getBoard: vi.fn(),
    addEntry: vi.fn(),
    moveEntry: vi.fn(),
    setEntryServings: vi.fn(),
    removeEntry: vi.fn(),
  };
});
vi.mock('#lib/shared/toast-store.svelte.ts', () => ({ showToast: vi.fn() }));

import { mealPlanStore as store } from './state.svelte';
import { ApiError, addEntry, getBoard, moveEntry, removeEntry, setEntryServings } from './api';
import { showToast } from '#lib/shared/toast-store.svelte.ts';
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

  it('S4: a failed remove after a week change splices nothing into the new week, and still says it failed', async () => {
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
    // The member confirmed that remove and the meal is still there: only the toast tells them, on any week
    // (fca#175 review 5987598100).
    expect(showToast).toHaveBeenCalledWith({ message: 'Something went wrong. Please try again.', kind: 'error' });
  });

  it('a failed add, move or servings change after a week change still says it failed', async () => {
    const down = new TypeError('Failed to fetch');
    const steps: Array<{ write: () => Promise<void>; message: string }> = [
      {
        write: () => {
          vi.mocked(addEntry).mockRejectedValueOnce(down);
          return store.addEntry({ date: '2026-10-18', mealType: 'snack', customMealName: 'Popcorn' });
        },
        message: "Couldn't add that meal right now.",
      },
      {
        write: () => {
          vi.mocked(moveEntry).mockRejectedValueOnce(down);
          return store.moveEntry(1, 1, W1, 'lunch', 11);
        },
        message: 'Something went wrong. Please try again.',
      },
      {
        write: () => {
          vi.mocked(setEntryServings).mockRejectedValueOnce(down);
          return store.setEntryServings(1, 1, 6, 11);
        },
        message: 'Something went wrong. Please try again.',
      },
    ];
    vi.mocked(getBoard).mockResolvedValue(board(W2, [entry(9, W2)]));
    for (const { write, message } of steps) {
      vi.mocked(showToast).mockClear();
      const writing = write();
      await store.changeWeek(W2); // the member steps to next week while the write is in flight
      await writing;
      expect(ids()).toEqual([9]); // nothing of W1 lands on W2's board
      expect(showToast).toHaveBeenCalledWith({ message, kind: 'error' });
      store.weekStart = W1;
      store.board = board(W1, [entry(1), entry(2)]);
    }
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

  // fca#175 review 5984614204: a Sunday picker confirms after the displayed week has moved to Monday's
  // (found with the midnight switch, which is gone; Back/Forward under the open picker still does it).
  // The add still says Sunday; it must not land on Monday's board.
  it("an add for another week's date (a picker left open across a week change) is saved but not merged", async () => {
    const sunday = '2026-10-18'; // the last day of W1
    vi.mocked(getBoard).mockResolvedValueOnce(board(W2, [entry(9, W2)]));
    await store.changeWeek(W2); // what App's week effect does when the URL moves to Monday's week
    vi.mocked(addEntry).mockResolvedValueOnce(entry(20, sunday, 'snack'));

    await store.addEntry({ date: sunday, mealType: 'snack', customMealName: 'Popcorn' });

    expect(addEntry).toHaveBeenCalledWith(expect.objectContaining({ date: sunday })); // the server still saves Sunday's
    expect(ids()).toEqual([9]);
    expect(store.weekMealRows()).not.toContain('snack'); // no phantom Snack row on Monday's week
  });

  it("an add for the board's own week is still merged", async () => {
    vi.mocked(addEntry).mockResolvedValueOnce(entry(20, '2026-10-18', 'snack'));
    await store.addEntry({ date: '2026-10-18', mealType: 'snack', customMealName: 'Popcorn' });
    expect(ids()).toEqual([1, 2, 20]);
  });

  // fca#175 review 5987398079 and the class it belongs to (design note fca-175-rollover-design-2026-11-20):
  // a dialog or a drag opened on one week can finish after the displayed week has changed (Back/Forward
  // under it). A write acts on the entry it was opened with, never on a lookup in the week on
  // screen. Entry 1 is W1's; the store is showing W2.
  describe('a write for an entry that is not on the displayed week', () => {
    beforeEach(async () => {
      vi.mocked(getBoard).mockResolvedValueOnce(board(W2, [entry(9, W2)]));
      await store.changeWeek(W2);
      vi.mocked(getBoard).mockClear();
    });

    it('remove (a Remove Meal dialog left open): still sends its DELETE with its token; the board shown is untouched', async () => {
      vi.mocked(removeEntry).mockResolvedValueOnce(undefined);
      await store.removeEntry(1, 1, 11);
      expect(removeEntry).toHaveBeenCalledWith(1, 1, 11);
      expect(ids()).toEqual([9]);
      expect(showToast).not.toHaveBeenCalled();
    });

    it('remove: a 409 still says the meal changed and reconciles the week on screen', async () => {
      vi.mocked(removeEntry).mockRejectedValueOnce(new ApiError(409, 'stale'));
      vi.mocked(getBoard).mockResolvedValueOnce(board(W2, [entry(9, W2)]));
      await store.removeEntry(1, 1, 11);
      expect(getBoard).toHaveBeenCalledWith(W2);
      expect(ids()).toEqual([9]);
      expect(showToast).toHaveBeenCalledWith(expect.objectContaining({ kind: 'info' }));
    });

    it('move (a drop into a slot of the week it was dragged on): still sends its PATCH; the board shown is untouched', async () => {
      vi.mocked(moveEntry).mockResolvedValueOnce({ ...entry(1), mealType: 'lunch', version: 99 });
      await store.moveEntry(1, 1, W1, 'lunch', 11);
      expect(moveEntry).toHaveBeenCalledWith(1, 1, { date: W1, mealType: 'lunch', version: 11 });
      expect(ids()).toEqual([9]);
    });

    it('servings (a Servings prompt left open; control, already addressed by id): sends its PATCH; the board shown is untouched', async () => {
      vi.mocked(setEntryServings).mockResolvedValueOnce({ ...entry(1), servings: 6, version: 99 });
      await store.setEntryServings(1, 1, 6, 11);
      expect(setEntryServings).toHaveBeenCalledWith(1, 1, 6, 11);
      expect(ids()).toEqual([9]);
    });
  });

  // A drag's zones belong to the week shown; a week change unmounts them mid-drag (the render gate), so no
  // finalize ever runs. Left set, the gate would keep the new week's zones from ever being built: every slot
  // drawn empty until a reload (drive S14 on 016fed8).
  it('a week change ends a drag, so the new week gets its zones', async () => {
    store.rebuildZones();
    store.zoneConsider(W1, 'dinner', []); // a drag of W1's dinner has begun
    expect(store.dragActive).toBe(true);
    vi.mocked(getBoard).mockResolvedValueOnce(board(W2, [entry(9, W2)]));

    await store.changeWeek(W2);

    expect(store.dragActive).toBe(false);
    store.rebuildZones(); // App's effect, now ungated
    expect(store.zoneFor(W2, 'dinner').map((e) => e.entryId)).toEqual([9]);
    store.dragActive = false; // never leak a set gate into another test (the store is a singleton)
  });
});
