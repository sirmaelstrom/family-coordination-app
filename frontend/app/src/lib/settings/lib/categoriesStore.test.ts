import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f, slice 3 (spec §5 row 6): the categories store on the Resource. The optimistic
// reorder goes through mutate(): a 409/4xx undoes and reloads, a 5xx/network failure undoes with no
// reload, and the undo never erases a later drag. The CRUD writes go through run().

// The REAL ApiError: the Resource classifies failures through its getters.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    getCategories: vi.fn(),
    createCategory: vi.fn(),
    updateCategory: vi.fn(),
    deleteCategory: vi.fn(),
    restoreCategory: vi.fn(),
    updateSortOrder: vi.fn(),
    categoryInUse: vi.fn(),
  };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { categoriesStore } from './categoriesStore.svelte';
import { ApiError, createCategory, getCategories, updateCategory, updateSortOrder } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { CategoryDto, CategoryListDto } from './types';

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

const cat = (categoryId: number, name: string): CategoryDto => ({
  categoryId,
  name,
  iconEmoji: null,
  color: '#808080',
  isDefault: false,
  sortOrder: categoryId,
  deletedAt: null,
});
const A = cat(1, 'Produce');
const B = cat(2, 'Dairy');
const C = cat(3, 'Bakery');
const list = (...active: CategoryDto[]): CategoryListDto => ({ active, deleted: [] });
const names = () => categoriesStore.active.map((c) => c.name);

describe('categoriesStore on the Resource (41337b6f slice 3)', () => {
  beforeEach(async () => {
    vi.resetAllMocks(); // also drops unconsumed *Once answers
    vi.mocked(getCategories).mockResolvedValueOnce(list(A, B, C));
    await categoriesStore.load();
    await flush();
    vi.resetAllMocks();
  });

  it('a 5xx reorder puts the old order back with an error toast and no reload', async () => {
    vi.mocked(updateSortOrder).mockRejectedValueOnce(new ApiError(503, 'down'));
    const reordering = categoriesStore.reorder([3, 1, 2]);
    expect(names()).toEqual(['Bakery', 'Produce', 'Dairy']); // optimistic, synchronous
    await reordering;
    await flush();
    expect(names()).toEqual(['Produce', 'Dairy', 'Bakery']);
    expect(getCategories).not.toHaveBeenCalled();
    expect(showToast).toHaveBeenCalledWith({ message: "Couldn't save that order right now.", kind: 'error' });
  });

  it('a 409 reorder puts the old order back and reloads the server order once', async () => {
    vi.mocked(updateSortOrder).mockRejectedValueOnce(new ApiError(409, 'stale'));
    vi.mocked(getCategories).mockResolvedValueOnce(list(B, A, C));
    await categoriesStore.reorder([3, 1, 2]);
    await flush();
    expect(getCategories).toHaveBeenCalledTimes(1);
    expect(names()).toEqual(['Dairy', 'Produce', 'Bakery']);
    expect(showToast).toHaveBeenCalledWith({ message: 'The categories changed — refreshing the order.', kind: 'info' });
  });

  it("a failed reorder's undo never erases a later drag", async () => {
    const first = deferred<void>();
    vi.mocked(updateSortOrder).mockReturnValueOnce(first.promise).mockResolvedValueOnce(undefined);
    const failing = categoriesStore.reorder([3, 1, 2]);
    await categoriesStore.reorder([2, 3, 1]); // a second drag lands while the first is pending
    expect(names()).toEqual(['Dairy', 'Bakery', 'Produce']);
    first.reject(new TypeError('Failed to fetch'));
    await failing;
    await flush();
    expect(names()).toEqual(['Dairy', 'Bakery', 'Produce']);
  });

  it('a successful update reloads once and toasts after the write', async () => {
    vi.mocked(updateCategory).mockResolvedValueOnce({ ...A, name: 'Fruit' });
    vi.mocked(getCategories).mockResolvedValueOnce(list({ ...A, name: 'Fruit' }, B, C));
    expect(await categoriesStore.update(1, { name: 'Fruit', iconEmoji: null, color: '#808080' })).toBe(true);
    await flush();
    expect(getCategories).toHaveBeenCalledTimes(1);
    expect(names()).toEqual(['Fruit', 'Dairy', 'Bakery']);
    expect(showToast).toHaveBeenCalledWith({ message: 'Category updated.', kind: 'success' });
  });

  it("add toasts the server's name on success, the server's message on failure", async () => {
    vi.mocked(createCategory).mockResolvedValueOnce(cat(4, 'Frozen'));
    vi.mocked(getCategories).mockResolvedValue(list(A, B, C, cat(4, 'Frozen')));
    expect(await categoriesStore.add({ name: ' Frozen ', iconEmoji: null, color: '#808080' })).toBe(true);
    expect(showToast).toHaveBeenLastCalledWith({ message: 'Category "Frozen" created.', kind: 'success' });

    vi.mocked(createCategory).mockRejectedValueOnce(new ApiError(400, 'Name is taken.'));
    expect(await categoriesStore.add({ name: 'Frozen', iconEmoji: null, color: '#808080' })).toBe(false);
    expect(showToast).toHaveBeenLastCalledWith({ message: 'Name is taken.', kind: 'error' });
    await flush();
  });
});
