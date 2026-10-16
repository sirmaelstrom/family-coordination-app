import { beforeEach, describe, expect, it, vi } from 'vitest';

// The board's load-vs-write ordering. Quest fe715e73 moved the load token into the store so an
// optimistic mutation could retire a GET already in flight; fca#103 added response-side retirement but
// left one ordering open — a GET that starts while a write is pending, answered BEFORE the write's
// response, still committed pre-write state. Quest 41337b6f moves the loader into a Resource owned by
// the store: no GET is dispatched while a chore write is pending (it is owed and runs once at settle),
// and one already in flight when a write starts is retired. These tests keep the fe715e73 / fca#103
// commit/no-commit contracts, now asserted on the BOARD (what commits) instead of on the old token.

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  getBoard: vi.fn(),
  claimChore: vi.fn(),
  deleteChore: vi.fn(),
  updateSubtask: vi.fn(),
  setDefaultView: vi.fn(),
}));
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { boardStore } from './state.svelte';
import { ApiError, claimChore, deleteChore, getBoard, setDefaultView, updateSubtask } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { ChoreBoardDto, ChoreDto } from './types';

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

function minimalChore(over: Partial<ChoreDto> = {}): ChoreDto {
  return {
    id: 1,
    name: 'Dishes',
    icon: '🍽',
    description: null,
    roomIds: [],
    recurrenceMode: 'Flexible',
    intervalDays: null,
    daysOfWeek: null,
    anchorDate: null,
    dueState: 'dueToday',
    colorTier: 'due',
    nextDueAt: null,
    snoozedUntil: null,
    isSnoozed: false,
    isClaimStale: false,
    effortTier: 'Quick',
    effortPoints: 1,
    ownerUserId: null,
    assigneeUserId: null,
    assignmentKind: 'none',
    claimedAt: null,
    lastCompletedAt: null,
    photoPath: null,
    version: 7,
    requiredCount: 1,
    completedCount: 0,
    roster: [],
    subtasks: [],
    ...over,
  } satisfies ChoreDto;
}

function minimalBoard(chores: ChoreDto[] = [minimalChore()]): ChoreBoardDto {
  return {
    chores,
    rooms: [],
    members: [],
    needsAttentionChoreIds: [],
    userDefaultView: null,
    callerCapacityTier: null,
    assignableUserIds: [],
  };
}

const claimed = (): ChoreDto => minimalChore({ assignmentKind: 'claimed', assigneeUserId: 42, version: 8 });

describe('chore board: loads are serialized against pending writes (41337b6f)', () => {
  beforeEach(async () => {
    await boardStore.settled(); // a singleton: let the last test's reconcile land first
    vi.resetAllMocks();
    boardStore.setBoard(minimalBoard());
  });

  it('a superseded board load never commits over the newer one', async () => {
    const older = deferred<ChoreBoardDto>();
    const newer = deferred<ChoreBoardDto>();
    vi.mocked(getBoard).mockReturnValueOnce(older.promise).mockReturnValueOnce(newer.promise);

    const a = boardStore.loadBoard();
    const b = boardStore.loadBoard();
    newer.resolve(minimalBoard([minimalChore({ name: 'newer' })]));
    await b;
    older.resolve(minimalBoard([minimalChore({ name: 'older' })]));
    await a;

    expect(boardStore.board?.chores[0].name).toBe('newer');
  });

  it('an optimistic mutation retires a GET already in flight (fe715e73): the pre-claim board never lands', async () => {
    const inflight = deferred<ChoreBoardDto>();
    vi.mocked(getBoard).mockReturnValueOnce(inflight.promise).mockResolvedValueOnce(minimalBoard([claimed()]));
    vi.mocked(claimChore).mockResolvedValueOnce(claimed());

    // A liveness GET goes out, then the user claims the chore before it lands.
    const loading = boardStore.loadBoard();
    await boardStore.claim(1);
    inflight.resolve(minimalBoard()); // the pre-claim board
    await loading;
    await boardStore.settled();

    expect(boardStore.board?.chores[0].assignmentKind).toBe('claimed');
    expect(getBoard).toHaveBeenCalledTimes(2); // the retired GET is owed once at settle
  });

  it('the fca#103 residual: a stale GET answered BEFORE the claim response never commits, even mid-write', async () => {
    const inflight = deferred<ChoreBoardDto>();
    const claim = deferred<ChoreDto>();
    vi.mocked(getBoard).mockReturnValueOnce(inflight.promise).mockResolvedValueOnce(minimalBoard([claimed()]));
    vi.mocked(claimChore).mockReturnValueOnce(claim.promise);

    const loading = boardStore.loadBoard();
    const claiming = boardStore.claim(1);
    expect(boardStore.board?.chores[0].assignmentKind).toBe('claimed'); // optimistic, synchronous
    inflight.resolve(minimalBoard()); // pre-claim truth, answered while the PATCH is on the wire
    await loading;
    expect(boardStore.board?.chores[0].assignmentKind).toBe('claimed'); // it did not land mid-write

    claim.resolve(claimed());
    await claiming;
    await boardStore.settled();
    expect(boardStore.board?.chores[0].assignmentKind).toBe('claimed');
  });

  it('a GET requested DURING a pending mutation is not dispatched; one runs at settle (fca#103 ordering)', async () => {
    const claim = deferred<ChoreDto>();
    vi.mocked(claimChore).mockReturnValueOnce(claim.promise);
    vi.mocked(getBoard).mockResolvedValueOnce(minimalBoard([claimed()]));

    const claiming = boardStore.claim(1);
    await boardStore.loadBoard(); // liveness fires while the PATCH is on the wire
    await boardStore.loadBoard();
    expect(getBoard).not.toHaveBeenCalled();

    claim.resolve(claimed());
    await claiming;
    await boardStore.settled();
    expect(getBoard).toHaveBeenCalledTimes(1);
  });

  it('remove retires in-flight reads — the deleted chore is never resurrected', async () => {
    const inflight = deferred<ChoreBoardDto>();
    vi.mocked(getBoard).mockReturnValueOnce(inflight.promise).mockResolvedValueOnce(minimalBoard([]));
    vi.mocked(deleteChore).mockResolvedValueOnce(undefined);

    const loading = boardStore.loadBoard();
    await boardStore.remove(1);
    expect(boardStore.board?.chores).toHaveLength(0);
    inflight.resolve(minimalBoard()); // still carries the deleted chore
    await loading;
    await boardStore.settled();

    expect(boardStore.board?.chores).toHaveLength(0);
  });

  it('a failed remove puts the card back at its index and refetches (reconcile on ANY error)', async () => {
    boardStore.setBoard(minimalBoard([minimalChore({ id: 1, name: 'a' }), minimalChore({ id: 2, name: 'b' }), minimalChore({ id: 3, name: 'c' })]));
    vi.mocked(deleteChore).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    const refetch = deferred<ChoreBoardDto>();
    vi.mocked(getBoard).mockReturnValueOnce(refetch.promise);

    await boardStore.remove(2);
    expect(boardStore.board?.chores.map((c) => c.name)).toEqual(['a', 'b', 'c']);
    expect(getBoard).toHaveBeenCalledTimes(1);
    expect(showToast).toHaveBeenCalledWith({ message: 'Something went wrong. Please try again.', kind: 'error' });
    refetch.resolve(minimalBoard([minimalChore({ id: 1, name: 'a' }), minimalChore({ id: 2, name: 'b' }), minimalChore({ id: 3, name: 'c' })]));
  });

  it('a conflicted claim rolls back, refetches, and keeps the chore pending until the refetch lands', async () => {
    vi.mocked(claimChore).mockRejectedValueOnce(new ApiError(409, 'stale'));
    const refetch = deferred<ChoreBoardDto>();
    vi.mocked(getBoard).mockReturnValueOnce(refetch.promise);

    await boardStore.claim(1);
    expect(boardStore.board?.chores[0].assignmentKind).toBe('none'); // rolled back
    expect(boardStore.isPending(1)).toBe(true); // a retry would resend the stale version
    await boardStore.claim(1);
    expect(claimChore).toHaveBeenCalledTimes(1);
    refetch.resolve(minimalBoard([claimed()]));
    await boardStore.settled();
    expect(boardStore.isPending(1)).toBe(false);
    expect(boardStore.board?.chores[0].assignmentKind).toBe('claimed');
  });

  it('an un-keyed subtask toggle still retires an in-flight GET, and never disables the card', async () => {
    const sub = { id: 9, title: 'rinse', isDone: false, sortOrder: 0, completedByUserId: null, completedAt: null };
    boardStore.setBoard(minimalBoard([minimalChore({ subtasks: [sub] as ChoreDto['subtasks'] })]));
    const inflight = deferred<ChoreBoardDto>();
    const put = deferred<ChoreDto['subtasks'][number]>();
    vi.mocked(getBoard).mockReturnValueOnce(inflight.promise).mockResolvedValueOnce(
      minimalBoard([minimalChore({ subtasks: [{ ...sub, isDone: true }] as ChoreDto['subtasks'] })]),
    );
    vi.mocked(updateSubtask).mockReturnValueOnce(put.promise);

    const loading = boardStore.loadBoard();
    const toggling = boardStore.toggleSubtask(1, 9, true);
    expect(boardStore.isPending(1)).toBe(false);
    inflight.resolve(minimalBoard([minimalChore({ subtasks: [sub] as ChoreDto['subtasks'] })])); // pre-toggle
    await loading;
    expect(boardStore.board?.chores[0].subtasks[0].isDone).toBe(true);
    put.resolve({ ...sub, isDone: true } as ChoreDto['subtasks'][number]);
    await toggling;
    await boardStore.settled();
    expect(boardStore.board?.chores[0].subtasks[0].isDone).toBe(true);
  });

  it('a liveness GET that overlaps the default-view PATCH never flips the default back', async () => {
    const inflight = deferred<ChoreBoardDto>();
    vi.mocked(getBoard).mockReturnValueOnce(inflight.promise).mockResolvedValueOnce({
      ...minimalBoard(),
      userDefaultView: 'mine',
    });
    vi.mocked(setDefaultView).mockResolvedValueOnce({ view: 'mine' });

    const loading = boardStore.loadBoard();
    await boardStore.saveDefaultView('mine');
    inflight.resolve(minimalBoard()); // userDefaultView: null — pre-PATCH
    await loading;
    await flush();
    await boardStore.settled();

    expect(boardStore.defaultView).toBe('mine');
  });
});
