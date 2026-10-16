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
import {
  ApiError,
  addItem,
  archiveList,
  createList,
  deleteItem,
  getList,
  listLists,
  patchItem,
  updateSortOrders,
} from './api';
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

  // ── council r7 ──────────────────────────────────────────────────────────────────────────────

  it("after a list switch, a delete targets the list ON SCREEN — never the other list's same-numbered item", async () => {
    // Item ids are per list. The user picks list 2; its load hasn't landed, so list 1's rows are still shown.
    const pending = deferred<ShoppingListDto>();
    vi.mocked(listLists).mockResolvedValue([summary(1), summary(2)]);
    vi.mocked(getList).mockReturnValueOnce(pending.promise);
    const switching = sl.selectList(2);
    expect(sl.currentListId).toBe(2);
    expect(sl.list?.id).toBe(1);

    vi.mocked(deleteItem).mockResolvedValueOnce(undefined);
    await sl.deleteItem(sl.list!.items[0]); // the shown item 1 — of list 1
    expect(deleteItem).toHaveBeenCalledWith(1, 1);
    pending.resolve(listDto(2));
    await switching;
    await sl.settled();
  });

  it("a write's answer never lands on another list's rows", async () => {
    const patch = deferred<ShoppingListItemDto>();
    vi.mocked(patchItem).mockReturnValueOnce(patch.promise);
    vi.mocked(listLists).mockResolvedValue([summary(1), summary(2)]);
    const checking = sl.setChecked(sl.list!.items[0], true);
    // While the PATCH is out, list 2 becomes the list on screen (its item 1 is a different item).
    // Same id AND the same token value as the item the PATCH sent — only the list identity tells them apart.
    sl.list = listDto(2, [item(1, { name: 'other list', version: 10 })]);
    patch.resolve(item(1, { isChecked: true, version: 11 }));
    await checking;
    expect(sl.list.items[0]).toMatchObject({ name: 'other list', isChecked: false, version: 10 });
    await sl.settled();
  });

  it('a failed reorder keeps its items pending until the refresh lands (no tap with an unreconciled token)', async () => {
    const two = [item(1), item(2)];
    sl.list = listDto(1, two);
    vi.mocked(updateSortOrders).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const refresh = deferred<ShoppingListDto>();
    vi.mocked(listLists).mockResolvedValue([summary(1)]);
    vi.mocked(getList).mockReturnValueOnce(refresh.promise);

    const reordering = sl.reorder('Other', [sl.list.items[1], sl.list.items[0]]);
    await flush();
    try {
      expect(sl.pendingIds.has(1)).toBe(true); // the write has failed, but its refresh is still out
      expect(await sl.setChecked(sl.list.items[0], true)).toBe('skipped');
      expect(patchItem).not.toHaveBeenCalled();
    } finally {
      refresh.resolve(listDto(1, two));
    }
    await reordering;
    await sl.settled(); // released when the refresh lands, not when the write returns
    expect(sl.pendingIds.size).toBe(0);
  });

  it('a failed reorder that settles while ANOTHER write is pending still holds its items until the refresh lands', async () => {
    sl.list = listDto(1, [item(1), item(2), item(3)]);
    const check = deferred<ShoppingListItemDto>();
    vi.mocked(patchItem).mockReturnValueOnce(check.promise);
    vi.mocked(updateSortOrders).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const refresh = deferred<ShoppingListDto>();
    vi.mocked(listLists).mockResolvedValue([summary(1)]);
    vi.mocked(getList).mockReturnValueOnce(refresh.promise);

    const checking = sl.setChecked(sl.list.items[2], true); // item 3's write keeps the window open
    await sl.reorder('Other', [sl.list.items[1], sl.list.items[0]]); // fails; no refresh can start yet
    expect(sl.pendingIds.has(1) && sl.pendingIds.has(2)).toBe(true);
    check.resolve(item(3, { isChecked: true, version: 11 }));
    await checking; // the last write settles: now the refresh starts
    expect(sl.pendingIds.has(1)).toBe(true);
    refresh.resolve(listDto(1, [item(1), item(2), item(3, { isChecked: true, version: 11 })]));
    await sl.settled();
    expect(sl.pendingIds.size).toBe(0);
  });

  it('a drop that moves an item another write still holds is not sent; the list refreshes instead', async () => {
    sl.list = listDto(1, [item(1), item(2)]);
    const check = deferred<ShoppingListItemDto>();
    vi.mocked(patchItem).mockReturnValueOnce(check.promise);
    vi.mocked(listLists).mockResolvedValue([summary(1)]);
    vi.mocked(getList).mockResolvedValue(listDto(1, [item(1, { isChecked: true, version: 11 }), item(2)]));

    const checking = sl.setChecked(sl.list.items[0], true);
    await sl.reorder('Other', [sl.list.items[1], sl.list.items[0]]); // item 1 is still saving
    expect(updateSortOrders).not.toHaveBeenCalled();
    expect(showToast).toHaveBeenCalledWith({ message: 'Still saving the last change — the list was refreshed.', kind: 'info' });
    check.resolve(item(1, { isChecked: true, version: 11 }));
    await checking;
    await sl.settled();
    expect(getList).toHaveBeenCalledTimes(1); // the owed refresh puts the server's order back
  });

  it('an edit during a reorder is refused (it would carry the pre-reorder token)', async () => {
    const answer = deferred<{ items: { id: number; versionBefore: number; version: number }[] }>();
    vi.mocked(updateSortOrders).mockReturnValueOnce(answer.promise as never);
    const reordering = sl.reorder('Other', [sl.list!.items[0]]);
    expect(await sl.editItem(sl.list!.items[0], { name: 'x', quantity: 1, unit: null, category: 'Other' })).toBe('skipped');
    answer.resolve({ items: [{ id: 1, versionBefore: 10, version: 11 }] });
    await reordering;
  });

  it('a failed summaries GET keeps the list that loaded (and the previous switcher)', async () => {
    vi.mocked(listLists).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    vi.mocked(getList).mockResolvedValueOnce(listDto(1, [item(1, { name: 'fresh' })]));
    await sl.load();
    expect(sl.list?.items[0].name).toBe('fresh');
    expect(sl.lists.map((l) => l.id)).toEqual([1, 2]);
    expect(sl.error).toBeNull();
  });

  it('a stalled summaries GET never holds the selected list off screen (slim-review on fca#160)', async () => {
    vi.mocked(listLists).mockReturnValueOnce(new Promise(() => {})); // never answers
    vi.mocked(getList).mockResolvedValueOnce(listDto(2));
    await sl.selectList(2);
    expect(sl.list?.id).toBe(2);
    expect(sl.lists.map((l) => l.id)).toEqual([1, 2]); // the previous switcher stays
    expect(sl.loading).toBe(false);
  });

  it('an item write opens the SUMMARIES window too: a summaries GET answered mid-write never commits', async () => {
    const poll = deferred<ShoppingListSummaryDto[]>();
    const patch = deferred<ShoppingListItemDto>();
    vi.mocked(listLists).mockReturnValueOnce(poll.promise).mockResolvedValue([summary(1), summary(2)]);
    vi.mocked(getList).mockResolvedValue(listDto(1, [item(1, { isChecked: true, version: 11 })]));
    vi.mocked(patchItem).mockReturnValueOnce(patch.promise);

    const polling = sl.load();
    const checking = sl.setChecked(sl.list!.items[0], true);
    poll.resolve([{ ...summary(1), uncheckedCount: 1 }]); // pre-check counts, answered while the PATCH is out
    await polling;
    expect(sl.lists.map((l) => l.id)).toEqual([1, 2]); // not replaced mid-write
    patch.resolve(item(1, { isChecked: true, version: 11 }));
    await checking;
    await sl.settled();
    expect(listLists).toHaveBeenCalledTimes(2); // the retired summaries load is owed once at settle
  });

  it('summaries that answer AFTER the list still land (slim-review delta on fca#160)', async () => {
    const slow = deferred<ShoppingListSummaryDto[]>();
    vi.mocked(listLists).mockReturnValueOnce(slow.promise);
    vi.mocked(getList).mockResolvedValueOnce(listDto(1));
    const loading = sl.load();
    await flush();
    expect(sl.list?.id).toBe(1); // the list is on screen without waiting
    slow.resolve([summary(1), summary(2), summary(3)]);
    await loading;
    expect(sl.lists.map((l) => l.id)).toEqual([1, 2, 3]); // and the late summaries are not discarded
  });

  it('a first visit with no list shows the spinner until the top list loads, never the empty state', async () => {
    const slowLists = deferred<ShoppingListSummaryDto[]>();
    vi.mocked(listLists).mockReturnValueOnce(slowLists.promise);
    vi.mocked(getList).mockResolvedValueOnce(listDto(4));
    const s = new ShoppingListState(null);
    const loading = s.load();
    await flush();
    expect(s.loading).toBe(true);
    expect(getList).not.toHaveBeenCalled();
    slowLists.resolve([summary(4)]);
    await loading;
    await flush();
    expect(s.list?.id).toBe(4);
    expect(s.loading).toBe(false);
  });

  it('a failed first summaries load (no list selected) shows the error and stops the spinner', async () => {
    vi.mocked(listLists).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const s = new ShoppingListState(null);
    await s.load();
    expect(s.loading).toBe(false);
    expect(s.error).toBe('Failed to fetch');
  });

  it('an initially empty household picks up a list created later on the next summaries load', async () => {
    vi.mocked(listLists).mockResolvedValueOnce([]).mockResolvedValueOnce([summary(8)]);
    vi.mocked(getList).mockResolvedValueOnce(listDto(8));
    const s = new ShoppingListState(null);
    await s.load();
    expect(s.list).toBeNull();
    expect(s.loading).toBe(false);
    await s.load(); // a liveness tick after another member created list 8
    await flush();
    expect(s.currentListId).toBe(8);
    expect(s.list?.id).toBe(8);
  });

  it('the 404 fallback tries each list once — stale summaries end in "not found", not an endless cycle', async () => {
    // Lists 1 and 2 are both gone, and the summaries GET keeps failing (the switcher still shows them).
    vi.mocked(listLists).mockRejectedValue(new TypeError('Failed to fetch'));
    vi.mocked(getList).mockRejectedValue(new ApiError(404, 'gone'));
    await sl.load();
    for (let i = 0; i < 6; i++) await flush();
    expect(getList).toHaveBeenCalledTimes(2); // list 1, then list 2 — and no more
    expect(sl.error).toBe('Shopping list not found.');
    expect(sl.list).toBeNull();
  });

  it('a first load retired by a write keeps the spinner (no "No active shopping lists" flash)', async () => {
    const first = deferred<ShoppingListSummaryDto[]>();
    vi.mocked(listLists).mockReturnValueOnce(first.promise);
    const s = new ShoppingListState(7);
    s.list = listDto(7); // a list object exists so a write can start; loading is still the first load's
    s.loading = true;
    const patch = deferred<ShoppingListItemDto>();
    vi.mocked(patchItem).mockReturnValueOnce(patch.promise);
    const loading = s.load();
    const checking = s.setChecked(s.list.items[0], true); // retires the first load
    expect(s.loading).toBe(true);
    first.resolve([summary(7)]);
    vi.mocked(getList).mockResolvedValue(listDto(7));
    vi.mocked(listLists).mockResolvedValue([summary(7)]);
    patch.resolve(item(1, { isChecked: true, version: 11 }));
    await loading;
    await checking;
    await s.settled();
    expect(s.loading).toBe(false);
  });

  it('the item dialog outcomes: add ok, edit conflict, add rejected', async () => {
    vi.mocked(addItem).mockResolvedValueOnce(item(5));
    expect(await sl.addItem({ name: 'eggs', quantity: 12, unit: null, category: 'Dairy' })).toBe('ok');
    expect(sl.list!.items.map((i) => i.id)).toContain(5);
    vi.mocked(patchItem).mockRejectedValueOnce(new ApiError(409, 'stale'));
    vi.mocked(listLists).mockResolvedValue([summary(1)]);
    vi.mocked(getList).mockResolvedValue(listDto(1));
    expect(await sl.editItem(sl.list!.items[0], { name: 'x', quantity: 1, unit: null, category: 'Other' })).toBe('conflict');
    await sl.settled();
    vi.mocked(addItem).mockRejectedValueOnce(new ApiError(400, 'bad'));
    expect(await sl.addItem({ name: '', quantity: null, unit: null, category: 'Other' })).toBe('rejected');
    await sl.settled();
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
