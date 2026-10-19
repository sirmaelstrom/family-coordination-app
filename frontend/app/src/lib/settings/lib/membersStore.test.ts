import { beforeEach, describe, expect, it, vi } from 'vitest';

// Quest 41337b6f, slice 3 (spec §5 row 7): the members store on the Resource — load + run().

// The REAL ApiError: the Resource classifies failures through its getters.
vi.mock('./api', async () => {
  const { ApiError } = await vi.importActual<typeof import('$lib/api/client')>('$lib/api/client');
  return { ApiError, getMembers: vi.fn(), addMember: vi.fn(), setWhitelist: vi.fn(), deleteMember: vi.fn() };
});
vi.mock('$lib/shared/toast-store.svelte', () => ({ showToast: vi.fn() }));

import { membersStore } from './membersStore.svelte';
import { ApiError, addMember, deleteMember, getMembers, setWhitelist } from './api';
import { showToast } from '$lib/shared/toast-store.svelte';
import type { MemberDto, MemberListDto } from './types';

/** Let the settle load (dispatched, not awaited, by the write) run to completion. */
const flush = () => new Promise<void>((r) => setTimeout(r, 0));

const member = (userId: number, email: string, isWhitelisted = true): MemberDto =>
  ({ userId, email, displayName: email, isWhitelisted }) as MemberDto;
const ME = member(1, 'me@example.test');
const NEW = member(2, 'new@example.test');
const list = (...members: MemberDto[]): MemberListDto => ({ currentUserId: 1, members });

describe('membersStore on the Resource (41337b6f slice 3)', () => {
  beforeEach(async () => {
    vi.resetAllMocks(); // also drops unconsumed *Once answers
    vi.mocked(getMembers).mockResolvedValueOnce(list(ME));
    await membersStore.load();
    await flush();
    vi.resetAllMocks();
  });

  it('add: the outcome toast, then one reload once the write settles', async () => {
    vi.mocked(addMember).mockResolvedValueOnce({ member: NEW, outcome: 'created' } as never);
    vi.mocked(getMembers).mockResolvedValueOnce(list(ME, NEW));
    expect(await membersStore.add('  new@example.test ')).toBe(true);
    await flush();
    expect(addMember).toHaveBeenCalledWith('new@example.test');
    expect(showToast).toHaveBeenCalledWith({ message: 'Added new@example.test.', kind: 'success' });
    expect(getMembers).toHaveBeenCalledTimes(1);
    expect(membersStore.members).toEqual([ME, NEW]);
  });

  it('a refused toggle toasts the server message and reconciles the list', async () => {
    vi.mocked(setWhitelist).mockRejectedValueOnce(new ApiError(409, 'You cannot disable the last active member.'));
    vi.mocked(getMembers).mockResolvedValueOnce(list(ME));
    await membersStore.toggle(1, false);
    await flush();
    expect(showToast).toHaveBeenCalledWith({ message: 'You cannot disable the last active member.', kind: 'error' });
    expect(getMembers).toHaveBeenCalledTimes(1);
  });

  it('remove toasts only on success', async () => {
    vi.mocked(deleteMember).mockRejectedValueOnce(new TypeError('Failed to fetch'));
    vi.mocked(getMembers).mockResolvedValue(list(ME));
    await membersStore.remove(2);
    expect(showToast).not.toHaveBeenCalledWith(expect.objectContaining({ kind: 'success' }));
    vi.mocked(deleteMember).mockResolvedValueOnce(undefined);
    await membersStore.remove(2);
    expect(showToast).toHaveBeenLastCalledWith({ message: 'Member removed.', kind: 'success' });
    await flush();
  });
});
