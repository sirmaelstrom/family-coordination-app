import { describe, expect, it } from 'vitest';
import { assignableMembers } from './assignable';
import type { MemberDto } from './types';

const m = (userId: number): MemberDto => ({ userId, displayName: `U${userId}`, initials: `U${userId}`, pictureUrl: null });
const members = [m(1), m(2), m(3)];

describe('assignableMembers (quest 59b0e3b6)', () => {
  it('offers only the members the board marks assignable', () => {
    expect(assignableMembers(members, [1, 3]).map((x) => x.userId)).toEqual([1, 3]);
  });

  it('keeps a disabled member in the role they already hold', () => {
    expect(assignableMembers(members, [1], [2, null]).map((x) => x.userId)).toEqual([1, 2]);
  });

  it("does not offer a disabled member in a role they don't hold (council r1: per-role lists)", () => {
    // User 2 is a disabled minder: kept in the minder picker, absent from the assignee picker.
    const minder = assignableMembers(members, [1, 3], [2]);
    const assignee = assignableMembers(members, [1, 3], [null]);
    expect(minder.map((x) => x.userId)).toContain(2);
    expect(assignee.map((x) => x.userId)).not.toContain(2);
  });
});
