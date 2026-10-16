import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f, slice 4 (spec §5 row 7): the two POLLING admin stores on the Resource. These are
// write-then-reload stores with no optimistic patch, so the old seq guard already kept a pre-write
// poll from landing after the post-write reload — the "parity pin" tests hold that (they pass on
// origin/master too). What changes is that a poll tick during a pending write is no longer
// dispatched at all; one load runs at settle (red on origin/master).

// The REAL ApiError: the Resource classifies failures through its getters.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return {
    ApiError,
    getFeedback: vi.fn(),
    markFeedbackRead: vi.fn(),
    markFeedbackResolved: vi.fn(),
    reopenFeedback: vi.fn(),
    getHouseholdRequests: vi.fn(),
    approveRequest: vi.fn(),
    rejectRequest: vi.fn(),
    createHousehold: vi.fn(),
  };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { feedbackStore } from './feedbackStore.svelte';
import { householdRequestsStore } from './householdRequestsStore.svelte';
import {
  ApiError,
  approveRequest,
  createHousehold,
  getFeedback,
  getHouseholdRequests,
  markFeedbackRead,
  markFeedbackResolved,
} from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { FeedbackDto, FeedbackListDto, HouseholdRequestDto, HouseholdRequestsDto, HouseholdSummaryDto } from './types';

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

const item = (isResolved: boolean): FeedbackDto => ({
  id: 1,
  type: 'bug',
  message: 'broken',
  currentPage: null,
  isRead: true,
  isResolved,
  createdAt: '2026-10-01T00:00:00Z',
  authorName: 'A',
  authorDeleted: false,
});
const feedback = (resolved: boolean): FeedbackListDto => ({ isSiteAdmin: true, items: [item(resolved)] });

const request = (status: HouseholdRequestDto['status']): HouseholdRequestDto => ({ id: 5, status }) as HouseholdRequestDto;
const SUMMARY: HouseholdSummaryDto = { householdId: 9, name: 'The Smiths', memberCount: 1, createdAt: '2026-10-01T00:00:00Z' };
const requests = (status: HouseholdRequestDto['status'], households: HouseholdSummaryDto[] = []): HouseholdRequestsDto => ({
  requests: [request(status)],
  households,
});

describe('feedbackStore on the Resource (41337b6f slice 4)', () => {
  beforeEach(async () => {
    vi.resetAllMocks(); // also drops unconsumed *Once answers
    vi.mocked(getFeedback).mockResolvedValueOnce(feedback(false));
    await feedbackStore.load();
    await flush();
    vi.resetAllMocks();
  });

  it('parity pin: a poll answered while a resolve is pending leaves the list as it was; one reload follows the write', async () => {
    const poll = deferred<FeedbackListDto>();
    const write = deferred<void>();
    vi.mocked(getFeedback).mockReturnValueOnce(poll.promise).mockResolvedValueOnce(feedback(true));
    vi.mocked(markFeedbackResolved).mockReturnValueOnce(write.promise);

    const polling = feedbackStore.load();
    const resolving = feedbackStore.markResolved(1);
    poll.resolve(feedback(false)); // pre-write truth, answered before the POST's response
    await polling;
    expect(feedbackStore.items[0].isResolved).toBe(false); // unchanged — but the stale poll did not commit
    expect(feedbackStore.loading).toBe(false);

    write.resolve();
    await resolving;
    await flush();
    expect(getFeedback).toHaveBeenCalledTimes(2);
    expect(feedbackStore.items[0].isResolved).toBe(true);
    expect(showToast).toHaveBeenCalledWith({ message: 'Marked as resolved.', kind: 'success' });
  });

  it('a poll tick during a pending write dispatches nothing; one GET runs at settle', async () => {
    const write = deferred<void>();
    vi.mocked(markFeedbackRead).mockReturnValueOnce(write.promise);
    vi.mocked(getFeedback).mockResolvedValueOnce(feedback(false));
    const marking = feedbackStore.markRead(1);
    await feedbackStore.load();
    await feedbackStore.load();
    expect(getFeedback).not.toHaveBeenCalled();
    write.resolve();
    await marking;
    await flush();
    expect(getFeedback).toHaveBeenCalledTimes(1);
  });

  it('a failed write toasts the server message and reconciles', async () => {
    vi.mocked(markFeedbackRead).mockRejectedValueOnce(new ApiError(404, 'Feedback not found.'));
    vi.mocked(getFeedback).mockResolvedValueOnce(feedback(false));
    await feedbackStore.markRead(1);
    await flush();
    expect(showToast).toHaveBeenCalledWith({ message: 'Feedback not found.', kind: 'error' });
    expect(getFeedback).toHaveBeenCalledTimes(1);
  });
});

describe('householdRequestsStore on the Resource (41337b6f slice 4)', () => {
  beforeEach(async () => {
    vi.resetAllMocks();
    vi.mocked(getHouseholdRequests).mockResolvedValueOnce(requests('pending'));
    await householdRequestsStore.load();
    await flush();
    vi.resetAllMocks();
  });

  it('parity pin: a poll in flight when approve starts, answered after it, never resurrects the pending request', async () => {
    const poll = deferred<HouseholdRequestsDto>();
    vi.mocked(getHouseholdRequests).mockReturnValueOnce(poll.promise).mockResolvedValueOnce(requests('approved', [SUMMARY]));
    vi.mocked(approveRequest).mockResolvedValueOnce(SUMMARY);

    const polling = householdRequestsStore.load();
    await householdRequestsStore.approve(5, 'The Smiths');
    await flush();
    expect(householdRequestsStore.requests[0].status).toBe('approved');

    poll.resolve(requests('pending')); // the pre-approve poll lands last
    await polling;
    expect(householdRequestsStore.requests[0].status).toBe('approved');
    expect(getHouseholdRequests).toHaveBeenCalledTimes(2);
    expect(showToast).toHaveBeenCalledWith({ message: 'Approved — created “The Smiths”.', kind: 'success' });
  });

  it('a 403 on the list GET is the access-denied signal, not an error', async () => {
    vi.mocked(getHouseholdRequests).mockRejectedValueOnce(new ApiError(403, 'Forbidden'));
    await householdRequestsStore.load();
    expect(householdRequestsStore.accessDenied).toBe(true);
    expect(householdRequestsStore.error).toBeNull();
    expect(householdRequestsStore.loading).toBe(false);
  });

  it('a refused create returns false, toasts, and clears `creating`', async () => {
    vi.mocked(createHousehold).mockRejectedValueOnce(new ApiError(409, 'That email already has a household.'));
    vi.mocked(getHouseholdRequests).mockResolvedValueOnce(requests('pending'));
    expect(await householdRequestsStore.createHousehold('X', 'x@example.test', '')).toBe(false);
    expect(householdRequestsStore.creating).toBe(false);
    expect(showToast).toHaveBeenCalledWith({ message: 'That email already has a household.', kind: 'error' });
    await flush();
  });
});
