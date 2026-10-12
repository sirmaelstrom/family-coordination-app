import { describe, expect, it } from 'vitest';
import { assignableMembers, peopleOnChore } from './assignable';
import type { ChoreDto, MemberDto } from './types';

const m = (userId: number): MemberDto => ({ userId, displayName: `U${userId}`, initials: `U${userId}`, pictureUrl: null });
const members = [m(1), m(2), m(3)];

describe('assignableMembers (quest 59b0e3b6)', () => {
  it('offers only the members the board marks assignable', () => {
    expect(assignableMembers(members, [1, 3]).map((x) => x.userId)).toEqual([1, 3]);
  });

  it('keeps a disabled member who is already on the chore, so an edit does not drop them', () => {
    expect(assignableMembers(members, [1], [2, null]).map((x) => x.userId)).toEqual([1, 2]);
  });
});

describe('peopleOnChore', () => {
  it('names the owner, the assignee and the roster', () => {
    const chore = { ownerUserId: 1, assigneeUserId: null, roster: [{ userId: 3, state: 'in' }] } as unknown as ChoreDto;
    expect(peopleOnChore(chore)).toEqual([1, 3]);
  });
});
