import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f, slice 2 (spec §5 row 4): the calendar-feed store on the Resource. The copy-once
// reveal is kept apart from the refreshable status, so the confirming load a write owes at settle
// cannot erase the URL that write just revealed; a status load that overlaps a write never commits.

// The REAL ApiError: the Resource classifies failures through its getters.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    getCalendarTokenStatus: vi.fn(),
    createOrRotateCalendarToken: vi.fn(),
    revokeCalendarToken: vi.fn(),
  };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { feedStore } from './feedStore.svelte';
import { ApiError, createOrRotateCalendarToken, getCalendarTokenStatus, revokeCalendarToken } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { CalendarTokenCreatedDto, CalendarTokenStatusDto } from './types';

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

const ACTIVE: CalendarTokenStatusDto = { active: true, createdAt: '2026-10-01T10:00:00Z' };
const NONE: CalendarTokenStatusDto = { active: false, createdAt: null };
const CREATED: CalendarTokenCreatedDto = { token: 't', url: 'https://example.test/feed.ics?t=new' };

describe('feedStore on the Resource (41337b6f slice 2)', () => {
  beforeEach(async () => {
    vi.resetAllMocks(); // also drops unconsumed *Once answers, so none leaks into the next step
    // The store is a singleton: start every test from a loaded "no token" card.
    vi.mocked(getCalendarTokenStatus).mockResolvedValueOnce(NONE);
    await feedStore.load();
    await flush();
    vi.resetAllMocks(); // also drops unconsumed *Once answers, so none leaks into the next step
  });

  it('the URL a create reveals survives the confirming status load the write owes', async () => {
    vi.mocked(createOrRotateCalendarToken).mockResolvedValueOnce(CREATED);
    vi.mocked(getCalendarTokenStatus).mockResolvedValueOnce(ACTIVE);

    await feedStore.createOrRotate();
    await flush();

    expect(getCalendarTokenStatus).toHaveBeenCalledTimes(1); // the confirming load ran
    expect(feedStore.phase.kind).toBe('revealed');
    expect(feedStore.url).toBe(CREATED.url);
  });

  it('a status load in flight when a create starts never commits, and never erases the reveal', async () => {
    const stale = deferred<CalendarTokenStatusDto>();
    vi.mocked(getCalendarTokenStatus).mockReturnValueOnce(stale.promise).mockResolvedValueOnce(ACTIVE);
    vi.mocked(createOrRotateCalendarToken).mockResolvedValueOnce(CREATED);

    const loading = feedStore.load();
    await feedStore.createOrRotate();
    stale.resolve(NONE); // pre-create truth: no token — it must not land after the reveal
    await loading;
    await flush();

    expect(feedStore.url).toBe(CREATED.url);
    expect(getCalendarTokenStatus).toHaveBeenCalledTimes(2); // the retired load is owed once at settle
  });

  it('a load requested while a revoke is pending is deferred, and a stale "active" never resurrects', async () => {
    vi.mocked(createOrRotateCalendarToken).mockResolvedValueOnce(CREATED);
    vi.mocked(getCalendarTokenStatus).mockResolvedValueOnce(ACTIVE);
    await feedStore.createOrRotate();
    await flush();
    vi.resetAllMocks(); // also drops unconsumed *Once answers, so none leaks into the next step

    const del = deferred<void>();
    vi.mocked(revokeCalendarToken).mockReturnValueOnce(del.promise);
    vi.mocked(getCalendarTokenStatus).mockResolvedValueOnce(NONE);

    const revoking = feedStore.revoke();
    await feedStore.load(); // e.g. the Retry path while the DELETE is pending
    expect(getCalendarTokenStatus).not.toHaveBeenCalled();

    del.resolve();
    await revoking;
    await flush();
    expect(getCalendarTokenStatus).toHaveBeenCalledTimes(1);
    expect(feedStore.phase.kind).toBe('none');
    expect(feedStore.url).toBeNull();
    expect(showToast).toHaveBeenCalledWith({ message: 'Subscription link revoked.', kind: 'success' });
  });

  it('copy-once is kept: a page (re)load forgets the URL even though the token is active', async () => {
    vi.mocked(createOrRotateCalendarToken).mockResolvedValueOnce(CREATED);
    vi.mocked(getCalendarTokenStatus).mockResolvedValueOnce(ACTIVE).mockResolvedValueOnce(ACTIVE);
    await feedStore.createOrRotate();
    await flush();
    expect(feedStore.url).toBe(CREATED.url);

    await feedStore.load();
    expect(feedStore.phase.kind).toBe('active');
    expect(feedStore.url).toBeNull();
  });

  it('a failed rotate forgets the URL on screen, toasts, and reconciles to the status', async () => {
    vi.mocked(createOrRotateCalendarToken).mockResolvedValueOnce(CREATED);
    vi.mocked(getCalendarTokenStatus).mockResolvedValueOnce(ACTIVE);
    await feedStore.createOrRotate();
    await flush();
    vi.resetAllMocks(); // also drops unconsumed *Once answers, so none leaks into the next step

    vi.mocked(createOrRotateCalendarToken).mockRejectedValueOnce(new ApiError(503, 'down'));
    vi.mocked(getCalendarTokenStatus).mockResolvedValueOnce(ACTIVE);
    await feedStore.createOrRotate();
    await flush();

    expect(feedStore.url).toBeNull();
    expect(feedStore.phase.kind).toBe('active');
    expect(getCalendarTokenStatus).toHaveBeenCalledTimes(1);
    expect(showToast).toHaveBeenCalledWith({ message: 'down', kind: 'error' });
    expect(feedStore.busy).toBe(false);
  });
});
