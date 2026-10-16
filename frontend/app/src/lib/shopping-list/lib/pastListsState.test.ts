import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f, slice 4 (spec §5 row 8): PastApp's state on two Resources. A delete runs inside
// both windows, so a detail GET or an archived-list GET that overlaps it never brings the deleted
// list back (before, `detailSeq` was never bumped by a delete).

vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    addItem: vi.fn(),
    deleteList: vi.fn(),
    getArchivedList: vi.fn(),
    listArchived: vi.fn(),
    listLists: vi.fn(),
    restoreList: vi.fn(),
  };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { PastListsState } from './pastListsState.svelte';
import { addItem, deleteList, getArchivedList, listArchived, listLists } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { ArchivedListSummaryDto, ShoppingListDto, ShoppingListItemDto, ShoppingListSummaryDto } from './types';

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

const row = (id: number): ArchivedListSummaryDto => ({
  id,
  name: `List ${id}`,
  isFavorite: false,
  itemCount: 2,
  uncheckedCount: 0,
  createdAt: '2026-10-01T00:00:00Z',
  hasMealPlan: false,
});
const ACTIVE: ShoppingListSummaryDto = { id: 100, name: 'This week', isFavorite: false, itemCount: 0, uncheckedCount: 0 };
const listItem = (id: number): ShoppingListItemDto => ({ id, name: `item ${id}`, quantity: null, unit: null, category: 'Other' }) as ShoppingListItemDto;
const detailOf = (id: number): ShoppingListDto => ({
  id,
  name: `List ${id}`,
  isFavorite: false,
  isArchived: true,
  hasMealPlan: false,
  items: [listItem(1), listItem(2)],
});

let past: PastListsState;

describe('PastListsState on two Resources (41337b6f slice 4)', () => {
  beforeEach(async () => {
    vi.resetAllMocks();
    past = new PastListsState();
    vi.mocked(listArchived).mockResolvedValueOnce([row(1), row(2)]);
    vi.mocked(listLists).mockResolvedValueOnce([ACTIVE]);
    await past.load();
    vi.resetAllMocks();
  });

  it('a detail GET in flight when its list is deleted never re-expands the deleted list', async () => {
    const detail = deferred<ShoppingListDto>();
    vi.mocked(getArchivedList).mockReturnValueOnce(detail.promise);
    vi.mocked(deleteList).mockResolvedValueOnce(undefined);
    vi.mocked(listArchived).mockResolvedValueOnce([row(2)]);
    vi.mocked(listLists).mockResolvedValueOnce([ACTIVE]);

    const expanding = past.toggleDetail(1);
    await past.remove(1);
    detail.resolve(detailOf(1)); // answered after the DELETE
    await expanding;
    await flush();

    expect(past.detail).toBeNull();
    expect(past.detailLoading).toBe(false);
    expect(getArchivedList).toHaveBeenCalledTimes(1); // the owed detail load fetches nothing
    expect(past.archived.map((l) => l.id)).toEqual([2]);
    expect(showToast).toHaveBeenCalledWith({ message: 'List permanently deleted', kind: 'info' });
  });

  it('parity pin: an archived-list GET in flight during a delete never lists the deleted list again', async () => {
    const stale = deferred<ArchivedListSummaryDto[]>();
    vi.mocked(listArchived).mockReturnValueOnce(stale.promise).mockResolvedValueOnce([row(2)]);
    vi.mocked(listLists).mockResolvedValue([ACTIVE]);
    vi.mocked(deleteList).mockResolvedValueOnce(undefined);

    const loading = past.load();
    await past.remove(1);
    stale.resolve([row(1), row(2)]);
    await loading;
    await flush();

    expect(past.archived.map((l) => l.id)).toEqual([2]);
    expect(listArchived).toHaveBeenCalledTimes(2);
  });

  it('a failed delete keeps the rows on screen while its reload is outstanding', async () => {
    vi.mocked(deleteList).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const reload = deferred<ArchivedListSummaryDto[]>();
    vi.mocked(listArchived).mockReturnValueOnce(reload.promise); // a stalled reconcile
    vi.mocked(listLists).mockResolvedValue([ACTIVE]);

    await past.remove(1);
    expect(listArchived).toHaveBeenCalledTimes(1);
    expect(past.loading).toBe(false); // no "Loading…" swap — the rows stay usable
    expect(past.archived.map((l) => l.id)).toEqual([1, 2]);
    expect(past.error).toBe('Failed to fetch');
    reload.resolve([row(1), row(2)]);
    await flush();
  });

  it('a collapse supersedes an expand still in flight', async () => {
    vi.mocked(getArchivedList).mockResolvedValueOnce(detailOf(1));
    await past.toggleDetail(1);
    expect(past.detail?.id).toBe(1);

    const slow = deferred<ShoppingListDto>();
    vi.mocked(getArchivedList).mockReturnValueOnce(slow.promise);
    const reexpand = past.toggleDetail(2);
    await past.toggleDetail(1); // collapse the one shown (1) while 2's GET is in flight
    slow.resolve(detailOf(2));
    await reexpand;
    expect(past.detail).toBeNull();
  });

  it('a partial add-picked failure keeps the rest selected and reports what landed', async () => {
    vi.mocked(getArchivedList).mockResolvedValueOnce(detailOf(1));
    await past.toggleDetail(1);
    past.togglePick(1);
    past.togglePick(2);
    vi.mocked(addItem).mockResolvedValueOnce({} as never).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    vi.mocked(listArchived).mockResolvedValue([row(1), row(2)]);
    vi.mocked(listLists).mockResolvedValue([ACTIVE]);

    await past.addPicked();
    await flush();
    expect([...past.pickedIds]).toEqual([2]);
    expect(past.adding).toBe(false);
    expect(showToast).toHaveBeenCalledWith({
      message: 'Added 1 of 2 — the rest stay selected; retry adds only those.',
      kind: 'info',
    });
  });
});
