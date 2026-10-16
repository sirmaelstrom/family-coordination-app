import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f WP-4: the dashboard's load ordering now comes from the Resource. A slow earlier load
// that resolves after a newer one must not overwrite it, and `loading` clears once the current load settles.

vi.mock('./api', () => ({ getDashboard: vi.fn() }));
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { DashboardStore } from './dashboardStore.svelte';
import { getDashboard } from './api';
import type { DashboardDto } from './types';

function dto(tag: string): DashboardDto {
  return { tag } as unknown as DashboardDto;
}

describe('dashboardStore load ordering', () => {
  beforeEach(() => vi.clearAllMocks());

  it('a superseded load neither overwrites the newer data nor surfaces its error', async () => {
    const store = new DashboardStore();
    let resolveOld!: (d: DashboardDto) => void;
    let rejectOld!: (e: unknown) => void;
    vi.mocked(getDashboard)
      .mockReturnValueOnce(
        new Promise<DashboardDto>((res, rej) => {
          resolveOld = res;
          rejectOld = rej;
        }),
      )
      .mockResolvedValueOnce(dto('new'));

    expect(store.loading).toBe(true);
    const older = store.load();
    await store.load();
    expect(store.data).toEqual(dto('new'));
    expect(store.loading).toBe(false);

    rejectOld(new Error('old failure'));
    void resolveOld;
    await older;
    expect(store.data).toEqual(dto('new'));
    expect(store.error).toBeNull();
    expect(getDashboard).toHaveBeenCalledTimes(2);
  });

  it('a current failure keeps the last good data and surfaces the error', async () => {
    const store = new DashboardStore();
    vi.mocked(getDashboard).mockResolvedValueOnce(dto('good')).mockRejectedValueOnce(new Error('down'));
    await store.load();
    await store.load();
    expect(store.data).toEqual(dto('good'));
    expect(store.error).toBe('down');
    expect(store.loading).toBe(false);
  });
});
