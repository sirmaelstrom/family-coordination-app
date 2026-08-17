import { beforeEach, describe, expect, it, vi } from 'vitest';

// The mutation-retires-reads contract (quest fe715e73): the board load token lives in the STORE so
// an optimistic mutation can retire a GET already in flight — the second race class, distinct from
// the load-vs-load races PR #88 fixed and slot 5 made testable.

vi.mock('./api', async (importOriginal) => ({
  ...(await importOriginal<object>()),
  claimChore: vi.fn(),
  deleteChore: vi.fn(),
}));
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { boardStore } from './state.svelte';
import { claimChore, deleteChore } from './api';
import type { ChoreBoardDto, ChoreDto } from './types';

function minimalBoard(): ChoreBoardDto {
  const chore = {
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
  } satisfies ChoreDto;
  return {
    chores: [chore],
    rooms: [],
    members: [],
    needsAttentionChoreIds: [],
    userDefaultView: null,
    callerCapacityTier: null,
  };
}

describe('board load token — mutations retire in-flight reads', () => {
  beforeEach(() => {
    vi.clearAllMocks();
    boardStore.setBoard(minimalBoard());
    boardStore.setRefresh(async () => {});
  });

  it('a newer load supersedes an older token', () => {
    const older = boardStore.beginLoad();
    const newer = boardStore.beginLoad();

    expect(boardStore.isCurrentLoad(older)).toBe(false);
    expect(boardStore.isCurrentLoad(newer)).toBe(true);
  });

  it('retireInFlightLoads invalidates an open token', () => {
    const seq = boardStore.beginLoad();
    boardStore.retireInFlightLoads();

    expect(boardStore.isCurrentLoad(seq)).toBe(false);
  });

  it('an optimistic mutation retires a GET already in flight', async () => {
    vi.mocked(claimChore).mockResolvedValueOnce({
      ...minimalBoard().chores[0],
      assignmentKind: 'claimed',
      assigneeUserId: 42,
      version: 8,
    });

    // A liveness GET goes out, then the user claims the chore before it lands.
    const inflight = boardStore.beginLoad();
    await boardStore.claim(1);

    expect(boardStore.isCurrentLoad(inflight)).toBe(false);
    // The loader's own contract: a stale token means the response is discarded, so the
    // pre-mutation board it carries can no longer undo the claim.
  });

  it('a GET that starts DURING a pending mutation is retired when the response applies', async () => {
    // The opposite ordering (slim-review on PR #103): the GET begins after the optimistic patch —
    // so it survives the first retirement — but it read the server BEFORE the mutation was
    // processed. The response-side retirement must invalidate it.
    let resolveClaim!: (v: ChoreDto) => void;
    vi.mocked(claimChore).mockReturnValueOnce(
      new Promise<ChoreDto>((res) => (resolveClaim = res)),
    );

    const claiming = boardStore.claim(1);
    const duringWrite = boardStore.beginLoad(); // liveness fires while the PATCH is on the wire
    resolveClaim({
      ...minimalBoard().chores[0],
      assignmentKind: 'claimed',
      assigneeUserId: 42,
      version: 8,
    });
    await claiming;

    expect(boardStore.isCurrentLoad(duringWrite)).toBe(false);
  });

  it('remove retires in-flight reads — the deleted-chore resurrection case', async () => {
    // remove() bypasses runOptimistic (slim-review finding on PR #103) and must retire on its own.
    vi.mocked(deleteChore).mockResolvedValueOnce(undefined);

    const inflight = boardStore.beginLoad();
    await boardStore.remove(1);

    expect(boardStore.isCurrentLoad(inflight)).toBe(false);
    expect(boardStore.board?.chores).toHaveLength(0);
  });
});
