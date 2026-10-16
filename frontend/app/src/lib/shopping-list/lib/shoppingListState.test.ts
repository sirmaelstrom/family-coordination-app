import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f, slice 8 (spec §5 row 11): the shopping list's state on ONE Resource that loads the list
// AND the summaries. Every write runs inside its window, so no GET that overlaps a write commits — in
// either response order (the old per-callsite `listLoadSeq`/`listsLoadSeq` + retireInFlightLoads() left
// the fca#103 residual open: a GET that STARTS during a write and is answered before its response).

vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    addItem: vi.fn(),
    archiveList: vi.fn(),
    clearChecked: vi.fn(),
    createList: vi.fn(),
    deleteItem: vi.fn(),
    generateFromMealPlan: vi.fn(),
    getList: vi.fn(),
    listLists: vi.fn(),
    patchItem: vi.fn(),
    regenerateList: vi.fn(),
    renameList: vi.fn(),
    toggleFavorite: vi.fn(),
    updateSortOrders: vi.fn(),
  };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { ShoppingListState } from './shoppingListState.svelte';
import { ApiError, archiveList, createList, getList, listLists, patchItem, updateSortOrders } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { ShoppingListDto, ShoppingListItemDto, ShoppingListSummaryDto } from './types';

function deferred<T>() {
  let resolve!: (value: T) => void;
  let reject!: (err: unknown) => void;
  const promise = new Promise<T>((res, rej) => {
    resolve = res;
    reject = rej;
  });
  return { promise, resolve, reject };
}
const flush = () => new Promise<void>((r) => setTimeout(r, 0));

const item = (id: number, over: Partial<ShoppingListItemDto> = {}): ShoppingListItemDto => ({
  id,
  name: `item ${id}`,
  quantity: 1,
  unit: null,
  category: 'Other',
  isChecked: false,
  checkedAt: null,
  sortOrder: id,
  addedByName: null,
  addedByInitials: null,
  addedByPictureUrl: null,
  version: 10,
  ...over,
});
const listDto = (id: number, items: ShoppingListItemDto[] = [item(1)]): ShoppingListDto =>
  ({ id, name: `List ${id}`, isFavorite: false, isArchived: false, hasMealPlan: false, items }) as ShoppingListDto;
const summary = (id: number): ShoppingListSummaryDto => ({ id, name: `List ${id}`, isFavorite: false, itemCount: 1, uncheckedCount: 1 });

let sl: ShoppingListState;
let switched: number[];

async function loaded(listId: number | null = 1): Promise<ShoppingListState> {
  switched = [];
  const s = new ShoppingListState(listId, (id) => switched.push(id));
  vi.mocked(listLists).mockResolvedValueOnce([summary(1), summary(2)]);
  if (listId != null) vi.mocked(getList).mockResolvedValueOnce(listDto(listId));
  await s.load();
  vi.resetAllMocks();
  return s;
}

describe('ShoppingListState on one Resource (41337b6f slice 8)', () => {
  beforeEach(async () => {
    vi.resetAllMocks();
    sl = await loaded(1);
  });

  it('the fca#103 residual: a poll answered BEFORE the check response never un-checks the item mid-write', async () => {
    const poll = deferred<ShoppingListDto>();
    const patch = deferred<ShoppingListItemDto>();
    vi.mocked(listLists).mockResolvedValue([summary(1), summary(2)]);
    vi.mocked(getList).mockReturnValueOnce(poll.promise).mockResolvedValueOnce(listDto(1, [item(1, { isChecked: true, version: 11 })]));
    vi.mocked(patchItem).mockReturnValueOnce(patch.promise);

    const polling = sl.load(); // a liveness tick on the wire
    const checking = sl.setChecked(sl.list!.items[0], true);
    expect(sl.list!.items[0].isChecked).toBe(true); // optimistic
    poll.resolve(listDto(1, [item(1)])); // pre-check truth, answered while the PATCH is out
    await polling;
    expect(sl.list!.items[0].isChecked).toBe(true); // it did not land mid-write

    patch.resolve(item(1, { isChecked: true, version: 11 }));
    expect(await checking).toBe('ok');
    await sl.settled();
    expect(sl.list!.items[0].isChecked).toBe(true);
    expect(getList).toHaveBeenCalledTimes(2); // the retired poll is owed once at settle
  });

  it('a poll tick during a pending write dispatches nothing; one load runs at settle', async () => {
    const patch = deferred<ShoppingListItemDto>();
    vi.mocked(patchItem).mockReturnValueOnce(patch.promise);
    vi.mocked(listLists).mockResolvedValue([summary(1)]);
    vi.mocked(getList).mockResolvedValue(listDto(1, [item(1, { quantity: 2, version: 11 })]));

    const bumping = sl.setQuantity(sl.list!.items[0], 1);
    await sl.load();
    await sl.load();
    expect(getList).not.toHaveBeenCalled();
    patch.resolve(item(1, { quantity: 2, version: 11 }));
    await bumping;
    await sl.settled();
    expect(getList).toHaveBeenCalledTimes(1); // the two deferred ticks coalesce into one owed load
    expect(sl.list!.items[0].quantity).toBe(2);
  });

  it('a 409 on a check undoes it, says why, refreshes, and keeps the item pending until the refresh lands', async () => {
    vi.mocked(patchItem).mockRejectedValueOnce(new ApiError(409, 'stale'));
    const refresh = deferred<ShoppingListDto>();
    vi.mocked(listLists).mockResolvedValue([summary(1)]);
    vi.mocked(getList).mockReturnValueOnce(refresh.promise);
    try {
      expect(await sl.setChecked(sl.list!.items[0], true)).toBe('conflict');
      expect(sl.list!.items[0].isChecked).toBe(false);
      expect(sl.pendingIds.has(1)).toBe(true);
      expect(showToast).toHaveBeenCalledWith({ message: 'That item changed — the list was refreshed.', kind: 'info' });
    } finally {
      refresh.resolve(listDto(1, [item(1, { isChecked: true, version: 12 })]));
    }
    await sl.settled();
    expect(sl.pendingIds.has(1)).toBe(false);
    expect(sl.list!.items[0].isChecked).toBe(true); // the other tab's change
  });

  it('a 404 on the selected list falls back to another list and loads it', async () => {
    vi.mocked(listLists).mockResolvedValue([summary(1), summary(2)]);
    vi.mocked(getList).mockRejectedValueOnce(new ApiError(404, 'gone')).mockResolvedValueOnce(listDto(2));

    await sl.load();
    await flush();
    await flush();

    expect(sl.currentListId).toBe(2);
    expect(switched).toEqual([2]);
    expect(sl.list?.id).toBe(2);
  });

  it("an obsolete 404 can't hijack a newer list selection", async () => {
    const stale = deferred<ShoppingListDto>();
    vi.mocked(listLists).mockResolvedValue([summary(1), summary(2), summary(3)]);
    vi.mocked(getList).mockReturnValueOnce(stale.promise).mockResolvedValueOnce(listDto(3));

    const old = sl.load();
    await sl.selectList(3); // the user picks list 3 while list 1's load is out
    stale.reject(new ApiError(404, 'gone'));
    await old;
    await flush();

    expect(sl.currentListId).toBe(3);
    expect(sl.list?.id).toBe(3);
  });

  it('with no list selected, the first visit defaults to the top list', async () => {
    vi.mocked(listLists).mockResolvedValue([summary(5), summary(6)]);
    vi.mocked(getList).mockResolvedValueOnce(listDto(5));
    const s = new ShoppingListState(null, (id) => switched.push(id));
    switched = [];
    await s.load();
    await flush();
    await flush();
    expect(s.currentListId).toBe(5);
    expect(s.list?.id).toBe(5);
  });

  it('creating a list switches to it, and the one reload at settle loads it', async () => {
    vi.mocked(createList).mockResolvedValueOnce(summary(9));
    vi.mocked(listLists).mockResolvedValue([summary(1), summary(9)]);
    vi.mocked(getList).mockResolvedValueOnce(listDto(9, []));

    expect(await sl.createList('Party')).toBe(true);
    await sl.settled();

    expect(sl.currentListId).toBe(9);
    expect(switched).toEqual([9]);
    expect(getList).toHaveBeenCalledTimes(1);
    expect(sl.list?.id).toBe(9);
  });

  it('archiving moves to the next list; a failure lands in the error banner', async () => {
    vi.mocked(archiveList).mockResolvedValueOnce(undefined);
    vi.mocked(listLists).mockResolvedValue([summary(2)]);
    vi.mocked(getList).mockResolvedValueOnce(listDto(2));
    await sl.archive();
    await sl.settled();
    expect(sl.currentListId).toBe(2);
    expect(sl.list?.id).toBe(2);

    vi.mocked(archiveList).mockRejectedValueOnce(new ApiError(500, 'boom'));
    vi.mocked(getList).mockResolvedValueOnce(listDto(2));
    await sl.archive();
    await sl.settled();
    expect(sl.error).toBe('boom');
    expect(sl.currentListId).toBe(2);
  });

  it('a reorder keeps the moved items pending until its answer brings their tokens; a failure refreshes', async () => {
    const two = [item(1, { version: 10 }), item(2, { version: 10 })];
    sl.list = listDto(1, two);
    const answer = deferred<{ items: { id: number; versionBefore: number; version: number }[] }>();
    vi.mocked(updateSortOrders).mockReturnValueOnce(answer.promise as never);

    const reordering = sl.reorder('Other', [sl.list.items[1], sl.list.items[0]]);
    expect(sl.pendingIds.has(1) && sl.pendingIds.has(2)).toBe(true);
    answer.resolve({ items: [{ id: 2, versionBefore: 10, version: 11 }, { id: 1, versionBefore: 10, version: 11 }] });
    await reordering;
    expect(sl.pendingIds.size).toBe(0);
    expect(sl.list.items.map((i) => i.version)).toEqual([11, 11]);

    vi.mocked(updateSortOrders).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    vi.mocked(listLists).mockResolvedValue([summary(1)]);
    vi.mocked(getList).mockResolvedValueOnce(listDto(1, two));
    await sl.reorder('Other', [sl.list.items[0], sl.list.items[1]]);
    await sl.settled();
    expect(getList).toHaveBeenCalledTimes(1); // 'on-error': the unsaved order is replaced by the server's
  });
});
